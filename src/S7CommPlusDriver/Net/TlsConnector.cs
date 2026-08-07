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
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Authentication.ExtendedProtection;
using System.Runtime.InteropServices;
using System.Threading;

namespace S7CommPlusDriver
{
    // Replaces OpenSSL P/Invoke with SslStream tunneled over the existing ISO packet framing.
    // Uses the same synchronous pattern as the original OpenSSL BIO connector: each ReadCompleted
    // call feeds one encrypted ISO payload, then immediately decrypts and delivers plaintext.
    internal sealed class TlsConnector : IDisposable
    {
        public interface IConnectorCallback
        {
            void WriteData(byte[] pData, int dataLength);
            void OnDataAvailable();
        }

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

        // Bridges SslStream I/O to the ISO packet send/receive path.
        private sealed class IsoTunnelStream : Stream
        {
            private readonly IConnectorCallback m_dataSink;
            private readonly Queue<DataBuffer> m_inboundQueue = new Queue<DataBuffer>();
            private readonly object m_lock = new object();
            private bool m_closed;

            public IsoTunnelStream(IConnectorCallback dataSink)
            {
                m_dataSink = dataSink;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

                lock (m_lock)
                {
                    while (!m_closed && m_inboundQueue.Count == 0)
                        Monitor.Wait(m_lock);

                    if (m_closed && m_inboundQueue.Count == 0)
                        return 0;

                    int copied = 0;
                    while (copied < count && m_inboundQueue.Count > 0)
                    {
                        DataBuffer head = m_inboundQueue.Peek();
                        int chunk = Math.Min(count - copied, head.Length);
                        Buffer.BlockCopy(head.Data, head.Offset, buffer, offset + copied, chunk);
                        head.Offset += chunk;
                        head.Length -= chunk;
                        copied += chunk;
                        if (head.Length == 0)
                            m_inboundQueue.Dequeue();
                    }
                    return copied;
                }
            }

            // Called by SslStream to send encrypted bytes — wraps them in ISO and sends.
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                m_dataSink.WriteData(copy, copy.Length);
            }

            public void AppendInboundData(byte[] data, int length)
            {
                lock (m_lock)
                {
                    if (m_closed) return;
                    m_inboundQueue.Enqueue(new DataBuffer(data, length));
                    Monitor.PulseAll(m_lock);
                }
            }

            public bool HasBufferedData()
            {
                lock (m_lock)
                    return m_inboundQueue.Count > 0;
            }

            public void CloseInbound()
            {
                lock (m_lock)
                {
                    m_closed = true;
                    Monitor.PulseAll(m_lock);
                }
            }

            protected override void Dispose(bool disposing)
            {
                CloseInbound();
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private readonly IConnectorCallback m_dataSink;
        private readonly IsoTunnelStream m_tunnel;
        private readonly SslStream m_sslStream;
        private readonly byte[] m_readBuffer = new byte[8192];
        private readonly Queue<DataBuffer> m_plaintextQueue = new Queue<DataBuffer>();
        private int m_bytesAvailable;
        private bool m_postHandshake;
        private bool m_disposed;

        public TlsConnector(IConnectorCallback dataSink)
        {
            m_dataSink = dataSink;
            m_tunnel = new IsoTunnelStream(dataSink);
            m_sslStream = new SslStream(m_tunnel, false, ValidateServerCertificate);
        }

        public void Activate(string targetHost)
        {
            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(targetHost) ? "s7commplus" : targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            };
            // AuthenticateAsClient drives the handshake synchronously; RunThread feeds data via ReadCompleted.
            m_sslStream.AuthenticateAsClient(authOptions);
            m_postHandshake = true;
        }

        // Called by S7Client.Send for outgoing application data.
        public void Write(byte[] pData, int dataLen)
        {
            m_sslStream.Write(pData, 0, dataLen);
            m_sslStream.Flush();
        }

        // Called by RunThread for each inbound ISO payload.
        // Pre-handshake: just feeds encrypted bytes so AuthenticateAsClient can consume them.
        // Post-handshake: feeds + immediately decrypts, mirroring the original OpenSSL BIO pattern.
        public void ReadCompleted(byte[] pData, int dataLen)
        {
            m_tunnel.AppendInboundData(pData, dataLen);

            if (!m_postHandshake)
                return;

            // Drain all plaintext that SslStream can produce from the record(s) just fed.
            int bytesRead;
            do
            {
                try
                {
                    bytesRead = m_sslStream.Read(m_readBuffer, 0, m_readBuffer.Length);
                }
                catch
                {
                    return;
                }

                if (bytesRead > 0)
                {
                    m_plaintextQueue.Enqueue(new DataBuffer(m_readBuffer, bytesRead));
                    m_bytesAvailable += bytesRead;
                    m_dataSink.OnDataAvailable();
                }
            }
            // Keep draining if SslStream buffered multiple records (e.g. after a server key-update).
            while (bytesRead > 0 && m_tunnel.HasBufferedData());
        }

        public int Receive(ref byte[] pData, int dataLength)
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
                m_bytesAvailable -= chunk;
                if (head.Length == 0)
                    m_plaintextQueue.Dequeue();
            }
            return bytesRead;
        }

        public byte[] getOMSExporterSecret()
        {
            var exportMethod = typeof(SslStream).GetMethod("ExportKeyingMaterial", new[] { typeof(string), typeof(byte[]), typeof(int) });
            if (exportMethod != null)
            {
                var exported = exportMethod.Invoke(m_sslStream, new object[] { "EXPERIMENTAL_OMS", null, 32 }) as byte[];
                if (exported != null && exported.Length == 32)
                    return exported;
            }

            var binding = m_sslStream.TransportContext?.GetChannelBinding(ChannelBindingKind.Unique);
            if (binding != null && binding.Size > 0)
            {
                byte[] data = new byte[binding.Size];
                Marshal.Copy(binding.DangerousGetHandle(), data, 0, data.Length);
                using (var sha = SHA256.Create())
                    return sha.ComputeHash(data).Take(32).ToArray();
            }

            return null;
        }

        private bool ValidateServerCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate, System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            m_tunnel.CloseInbound();
            try { m_sslStream.Dispose(); } catch { }
            try { m_tunnel.Dispose(); } catch { }
        }
    }
}
