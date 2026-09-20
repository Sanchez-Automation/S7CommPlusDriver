#region License
/******************************************************************************
 * S7CommPlusDriver
 *
 * Copyright (C) 2026
 *
 * This file is part of S7CommPlusDriver.
 *
 * S7CommPlusDriver is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 /****************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace S7CommPlusDriver
{
    // TLS on top of the existing ISO packet framing, using BouncyCastle's non-blocking TLS protocol.
    // BouncyCastle is used instead of SslStream because the PLC legitimation needs the TLS exporter
    // secret (label "EXPERIMENTAL_OMS"), which SslStream does not expose on any .NET version.
    // Same synchronous pattern as the original OpenSSL BIO connector: each ReadCompleted call feeds one
    // encrypted ISO payload and immediately decrypts it and delivers plaintext.
    internal sealed class TlsConnector : IDisposable
    {
        public interface IConnectorCallback
        {
            void WriteData(byte[] pData, int dataLength);
            void OnDataAvailable();
        }

        private const int HandshakeTimeoutMs = 15000;
        private const int ReadChunkSize = 8192;

        private sealed class DataBuffer
        {
            public readonly byte[] Data;
            public int Offset;
            public int Length;

            public DataBuffer(byte[] data, int length)
            {
                Data = new byte[length];
                Buffer.BlockCopy(data, 0, Data, 0, length);
                Offset = 0;
                Length = length;
            }
        }

        // Accepts every server certificate: the PLC's certificate is self-signed, and the original
        // OpenSSL connector did no validation either.
        private sealed class AcceptAllAuthentication : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) { return null; }
        }

        private sealed class PlcTlsClient : DefaultTlsClient
        {
            private TlsContext m_context;

            public PlcTlsClient() : base(new BcTlsCrypto()) { }

            public byte[] OmsExporterSecret { get; private set; }

            public override void Init(TlsClientContext context)
            {
                base.Init(context);
                m_context = context;
            }

            // BouncyCastle only allows key export from this callback, so capture it here.
            // RFC 5705 / RFC 8446 exporter, label "EXPERIMENTAL_OMS", no context, 32 bytes.
            public override void NotifyHandshakeComplete()
            {
                base.NotifyHandshakeComplete();
                OmsExporterSecret = m_context.ExportKeyingMaterial("EXPERIMENTAL_OMS", null, 32);
            }

            protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions()
            {
                return Org.BouncyCastle.Tls.ProtocolVersion.TLSv13.DownTo(Org.BouncyCastle.Tls.ProtocolVersion.TLSv12);
            }

            public override TlsAuthentication GetAuthentication()
            {
                return new AcceptAllAuthentication();
            }
        }

        private readonly IConnectorCallback m_dataSink;
        private readonly object m_lock = new object();
        private readonly TlsClientProtocol m_protocol = new TlsClientProtocol();
        private readonly PlcTlsClient m_client = new PlcTlsClient();
        private readonly Queue<DataBuffer> m_plaintextQueue = new Queue<DataBuffer>();
        private Exception m_failure;
        private bool m_disposed;

        public TlsConnector(IConnectorCallback dataSink)
        {
            m_dataSink = dataSink;
        }

        // Starts the handshake and blocks until it is complete. The handshake is driven by
        // RunThread, which feeds the server's records through ReadCompleted.
        public void Activate(string targetHost)
        {
            lock (m_lock)
            {
                m_protocol.Connect(m_client);
                FlushOutput();

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(HandshakeTimeoutMs);
                while (!m_protocol.IsConnected)
                {
                    if (m_failure != null)
                        throw new InvalidOperationException("TLS handshake failed", m_failure);
                    if (m_disposed || m_protocol.IsClosed)
                        throw new InvalidOperationException("TLS connection closed during handshake");

                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0)
                        throw new TimeoutException("TLS handshake timed out");
                    System.Threading.Monitor.Wait(m_lock, remaining);
                }
            }
        }

        // Called by S7Client.Send for outgoing application data.
        public void Write(byte[] pData, int dataLen)
        {
            lock (m_lock)
            {
                m_protocol.WriteApplicationData(pData, 0, dataLen);
                FlushOutput();
            }
        }

        // Called by RunThread for each inbound ISO payload. Feeds the encrypted bytes to the TLS
        // engine, sends anything it wants to send back, and delivers any decrypted application data.
        public void ReadCompleted(byte[] pData, int dataLen)
        {
            int chunks = 0;
            lock (m_lock)
            {
                if (m_disposed)
                    return;
                try
                {
                    m_protocol.OfferInput(pData, 0, dataLen);
                    FlushOutput();

                    byte[] buf = new byte[ReadChunkSize];
                    int bytesRead;
                    while (m_protocol.GetAvailableInputBytes() > 0 &&
                           (bytesRead = m_protocol.ReadInput(buf, 0, buf.Length)) > 0)
                    {
                        m_plaintextQueue.Enqueue(new DataBuffer(buf, bytesRead));
                        chunks++;
                    }
                }
                catch (Exception ex)
                {
                    m_failure = ex;
                }
                System.Threading.Monitor.PulseAll(m_lock);
            }

            // Notify outside the lock; each chunk is at most ReadChunkSize, and the consumer reads up
            // to ReadChunkSize per notification.
            for (int i = 0; i < chunks; i++)
                m_dataSink.OnDataAvailable();
        }

        public int Receive(ref byte[] pData, int dataLength)
        {
            lock (m_lock)
            {
                int bytesRead = 0;
                while (bytesRead < dataLength && m_plaintextQueue.Count > 0)
                {
                    DataBuffer head = m_plaintextQueue.Peek();
                    int chunk = Math.Min(dataLength - bytesRead, head.Length);
                    Buffer.BlockCopy(head.Data, head.Offset, pData, bytesRead, chunk);
                    head.Offset += chunk;
                    head.Length -= chunk;
                    bytesRead += chunk;
                    if (head.Length == 0)
                        m_plaintextQueue.Dequeue();
                }
                return bytesRead;
            }
        }

        // Must be called with m_lock held.
        private void FlushOutput()
        {
            int available;
            while ((available = m_protocol.GetAvailableOutputBytes()) > 0)
            {
                byte[] data = new byte[available];
                int n = m_protocol.ReadOutput(data, 0, data.Length);
                if (n <= 0)
                    break;
                m_dataSink.WriteData(data, n);
            }
        }

        // Key material for the PLC legitimation, captured when the handshake completed.
        public byte[] getOMSExporterSecret()
        {
            lock (m_lock)
            {
                byte[] secret = m_client.OmsExporterSecret;
                return secret == null ? null : (byte[])secret.Clone();
            }
        }

        public void Dispose()
        {
            lock (m_lock)
            {
                if (m_disposed)
                    return;
                m_disposed = true;
                try { m_protocol.Close(); } catch { }
                System.Threading.Monitor.PulseAll(m_lock);
            }
        }
    }
}
