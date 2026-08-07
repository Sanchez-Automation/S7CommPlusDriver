using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace DriverTest
{
    class Program
    {
        private static readonly string[] DefaultSymbols =
        {
            "DriverTest.Type.Bit_Bool",
            "DriverTest.Type.Byte_Val",
            "DriverTest.Type.Word_Val",
            "DriverTest.Type.DWord_Val",
            "DriverTest.Type.LWord_Val",
            "DriverTest.Type.SInt_Val",
            "DriverTest.Type.Int_Val",
            "DriverTest.Type.DInt_Val",
            "DriverTest.Type.LInt_Val",
            "DriverTest.Type.USInt_Val",
            "DriverTest.Type.UInt_Val",
            "DriverTest.Type.UDInt_Val",
            "DriverTest.Type.ULInt_Val",
            "DriverTest.Type.Real_Val",
            "DriverTest.Type.LReal_Val",
            "DriverTest.Type.Char_Val",
            "DriverTest.Type.WChar_Val",
            "DriverTest.Type.String_Val",
            "DriverTest.Type.WString_Val",
            "DriverTest.Type.Time_Val",
            "DriverTest.Type.LTime_Val",
            "DriverTest.Type.Date_Val",
            "DriverTest.Type.TOD_Val",
            "DriverTest.Type.LTOD_Val",
            "DriverTest.Type.DTL_Val.YEAR",
            "DriverTest.Type.DTL_Val.MONTH",
            "DriverTest.Type.DTL_Val.DAY",
            "DriverTest.Type.DTL_Val.WEEKDAY",
            "DriverTest.Type.DTL_Val.HOUR",
            "DriverTest.Type.DTL_Val.MINUTE",
            "DriverTest.Type.DTL_Val.SECOND",
            "DriverTest.Type.DTL_Val.NANOSECOND"
        };

        private sealed class DtlValue
        {
            public DateTime Value;
            public uint Nanosecond;

            public DtlValue(DateTime value, uint nanosecond)
            {
                Value = value;
                Nanosecond = nanosecond;
            }
        }

        private sealed class TodValue
        {
            public uint Milliseconds;

            public TodValue(uint milliseconds)
            {
                Milliseconds = milliseconds;
            }
        }

        private sealed class LTodValue
        {
            public ulong Nanoseconds;

            public LTodValue(ulong nanoseconds)
            {
                Nanoseconds = nanoseconds;
            }
        }

        static void Main(string[] args)
        {
            string hostIp = args.Length >= 1 ? args[0] : "192.168.1.30";
            string password = args.Length >= 2 ? args[1] : "";
            string username = args.Length >= 3 ? args[2] : "";
            string[] symbols = args.Length >= 4
                ? args[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray()
                : DefaultSymbols;

            Console.WriteLine("DriverTest - START");
            Console.WriteLine("DriverTest - Target PLC: " + hostIp);

            int res;
            var conn = new S7CommPlusConnection();
            res = conn.Connect(hostIp, password, username);
            if (res != 0)
            {
                Console.WriteLine("DriverTest - Connect failed: " + res);
                return;
            }

            Console.WriteLine("DriverTest - Connected");
            res = conn.Browse(out List<VarInfo> vars);
            if (res != 0)
            {
                Console.WriteLine("DriverTest - Browse failed: " + res);
                conn.Disconnect();
                return;
            }

            var varByName = vars.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("DriverTest - Symbolic verification list:");
            foreach (string symbol in symbols)
            {
                Console.WriteLine("  - " + symbol);
            }

            foreach (string symbol in symbols)
            {
                if (!varByName.TryGetValue(symbol, out VarInfo info))
                {
                    Console.WriteLine("[SKIP] Symbol not found: " + symbol);
                    PrintCandidateSymbols(symbol, vars);
                    failCount++;
                    continue;
                }

                PlcTag tag;
                try
                {
                    tag = PlcTags.TagFactory(info.Name, new ItemAddress(info.AccessSequence), info.Softdatatype);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] TagFactory failed for " + symbol + ": " + ex.Message);
                    failCount++;
                    continue;
                }

                if (!TryReadWriteVerify(conn, tag, out string reason))
                {
                    Console.WriteLine("[FAIL] " + symbol + " -> " + reason);
                    failCount++;
                }
                else
                {
                    Console.WriteLine("[PASS] " + symbol);
                    passCount++;
                }
            }

            conn.Disconnect();
            Console.WriteLine("DriverTest - Summary: pass=" + passCount + " fail=" + failCount);
            Environment.ExitCode = failCount == 0 ? 0 : 1;
        }

        private static void PrintCandidateSymbols(string requestedSymbol, List<VarInfo> vars)
        {
            string[] parts = requestedSymbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            string[] needles = parts.Length > 1
                ? new[] { parts[0], parts[parts.Length - 1] }
                : new[] { requestedSymbol };

            var candidates = vars
                .Where(v => needles.Any(n => v.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(8)
                .Select(v => v.Name)
                .ToList();

            if (candidates.Count == 0)
            {
                candidates = vars.Take(8).Select(v => v.Name).ToList();
            }

            Console.WriteLine("      Candidates:");
            foreach (var c in candidates)
            {
                Console.WriteLine("        * " + c);
            }
        }

        private static bool TryReadWriteVerify(S7CommPlusConnection conn, PlcTag tag, out string reason)
        {
            reason = "";
            var tags = new List<PlcTag> { tag };
            int res = conn.ReadTags(tags);
            if (res != 0)
            {
                reason = "Read failed with code " + res;
                return false;
            }

            if (!TryMutateTagValue(tag, out object originalValue, out object writeValue, out object restoreValue))
            {
                reason = "Datatype not supported by this verification harness";
                return false;
            }

            res = conn.WriteTags(tags);
            if (res != 0)
            {
                reason = "Write failed with code " + res;
                return false;
            }
            Console.WriteLine("      WRITE   " + tag.Name + " = " + ValueToText(writeValue));

            res = conn.ReadTags(tags);
            if (res != 0)
            {
                reason = "Verify read failed with code " + res;
                return false;
            }

            object verifiedValue = GetTagValue(tag);
            if (!ValuesEqual(verifiedValue, writeValue))
            {
                reason = "Verify mismatch (expected=" + ValueToText(writeValue) + ", got=" + ValueToText(verifiedValue) + ")";
                return false;
            }
            Console.WriteLine("      VERIFY  " + tag.Name + " = " + ValueToText(verifiedValue));

            // Keep the modified value visible briefly for PLC-side online monitoring.
            Thread.Sleep(800);

            // Best-effort restore so repeated test runs remain stable.
            SetTagValue(tag, restoreValue);
            conn.WriteTags(tags);
            Console.WriteLine("      RESTORE " + tag.Name + " = " + ValueToText(restoreValue));

            return true;
        }

        private static bool TryMutateTagValue(PlcTag tag, out object originalValue, out object writeValue, out object restoreValue)
        {
            originalValue = null;
            writeValue = null;
            restoreValue = null;

            switch (tag)
            {
                case PlcTagBool t:
                    originalValue = t.Value;
                    writeValue = !t.Value;
                    restoreValue = t.Value;
                    t.Value = (bool)writeValue;
                    return true;
                case PlcTagByte t:
                    originalValue = t.Value;
                    writeValue = (byte)(t.Value == byte.MaxValue ? 0 : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (byte)writeValue;
                    return true;
                case PlcTagUSInt t:
                    originalValue = t.Value;
                    writeValue = (byte)(t.Value == byte.MaxValue ? 0 : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (byte)writeValue;
                    return true;
                case PlcTagSInt t:
                    originalValue = t.Value;
                    writeValue = (sbyte)(t.Value == sbyte.MaxValue ? sbyte.MinValue : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (sbyte)writeValue;
                    return true;
                case PlcTagWord t:
                    originalValue = t.Value;
                    writeValue = (ushort)(t.Value == ushort.MaxValue ? 0 : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (ushort)writeValue;
                    return true;
                case PlcTagUInt t:
                    originalValue = t.Value;
                    writeValue = (ushort)(t.Value == ushort.MaxValue ? 0 : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (ushort)writeValue;
                    return true;
                case PlcTagInt t:
                    originalValue = t.Value;
                    writeValue = (short)(t.Value == short.MaxValue ? short.MinValue : t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (short)writeValue;
                    return true;
                case PlcTagDWord t:
                    originalValue = t.Value;
                    writeValue = t.Value == uint.MaxValue ? 0u : t.Value + 1u;
                    restoreValue = t.Value;
                    t.Value = (uint)writeValue;
                    return true;
                case PlcTagUDInt t:
                    originalValue = t.Value;
                    writeValue = t.Value == uint.MaxValue ? 0u : t.Value + 1u;
                    restoreValue = t.Value;
                    t.Value = (uint)writeValue;
                    return true;
                case PlcTagDInt t:
                    originalValue = t.Value;
                    writeValue = t.Value == int.MaxValue ? int.MinValue : t.Value + 1;
                    restoreValue = t.Value;
                    t.Value = (int)writeValue;
                    return true;
                case PlcTagLWord t:
                    originalValue = t.Value;
                    writeValue = t.Value == ulong.MaxValue ? 0UL : t.Value + 1UL;
                    restoreValue = t.Value;
                    t.Value = (ulong)writeValue;
                    return true;
                case PlcTagULInt t:
                    originalValue = t.Value;
                    writeValue = t.Value == ulong.MaxValue ? 0UL : t.Value + 1UL;
                    restoreValue = t.Value;
                    t.Value = (ulong)writeValue;
                    return true;
                case PlcTagLInt t:
                    originalValue = t.Value;
                    writeValue = t.Value == long.MaxValue ? long.MinValue : t.Value + 1L;
                    restoreValue = t.Value;
                    t.Value = (long)writeValue;
                    return true;
                case PlcTagReal t:
                    originalValue = t.Value;
                    writeValue = t.Value + 1.0f;
                    restoreValue = t.Value;
                    t.Value = (float)writeValue;
                    return true;
                case PlcTagLReal t:
                    originalValue = t.Value;
                    writeValue = t.Value + 1.0;
                    restoreValue = t.Value;
                    t.Value = (double)writeValue;
                    return true;
                case PlcTagChar t:
                    originalValue = t.Value;
                    writeValue = t.Value == 'Z' ? 'A' : (char)(t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (char)writeValue;
                    return true;
                case PlcTagWChar t:
                    originalValue = t.Value;
                    writeValue = t.Value == 'Z' ? 'A' : (char)(t.Value + 1);
                    restoreValue = t.Value;
                    t.Value = (char)writeValue;
                    return true;
                case PlcTagString t:
                    originalValue = t.Value;
                    writeValue = "GH" + (string.IsNullOrEmpty(t.Value) ? "_test" : t.Value + "_test");
                    restoreValue = t.Value;
                    t.Value = ((string)writeValue).Length > 254 ? ((string)writeValue).Substring(0, 254) : (string)writeValue;
                    return true;
                case PlcTagWString t:
                    originalValue = t.Value;
                    writeValue = "GH" + (string.IsNullOrEmpty(t.Value) ? "_test" : t.Value + "_test");
                    restoreValue = t.Value;
                    t.Value = ((string)writeValue).Length > 254 ? ((string)writeValue).Substring(0, 254) : (string)writeValue;
                    return true;
                case PlcTagTime t:
                    originalValue = t.Value;
                    writeValue = t.Value > int.MaxValue - 1000 ? 0 : t.Value + 1000;
                    restoreValue = t.Value;
                    t.Value = (int)writeValue;
                    return true;
                case PlcTagLTime t:
                    originalValue = t.Value;
                    writeValue = t.Value > long.MaxValue - 1000000 ? 0L : t.Value + 1000000L;
                    restoreValue = t.Value;
                    t.Value = (long)writeValue;
                    return true;
                case PlcTagDate t:
                    originalValue = t.Value;
                    writeValue = t.Value >= new DateTime(2169, 6, 5) ? new DateTime(1990, 1, 1) : t.Value.AddDays(1);
                    restoreValue = t.Value;
                    t.Value = (DateTime)writeValue;
                    return true;
                case PlcTagTimeOfDay t:
                    originalValue = new TodValue(t.Value);
                    writeValue = new TodValue((uint)((t.Value + 1000) % 86400000));
                    restoreValue = new TodValue(t.Value);
                    t.Value = ((TodValue)writeValue).Milliseconds;
                    return true;
                case PlcTagLTOD t:
                    originalValue = new LTodValue(t.Value);
                    writeValue = new LTodValue((t.Value + 1000000000UL) % 86400000000000UL);
                    restoreValue = new LTodValue(t.Value);
                    t.Value = ((LTodValue)writeValue).Nanoseconds;
                    return true;
                case PlcTagDTL t:
                    originalValue = new DtlValue(t.Value, t.ValueNanosecond);
                    DateTime next = t.Value >= new DateTime(2262, 4, 11, 23, 47, 15) ? new DateTime(1970, 1, 1) : t.Value.AddSeconds(1);
                    uint nextNs = t.ValueNanosecond >= 999000000 ? 0U : t.ValueNanosecond + 1000000U;
                    writeValue = new DtlValue(next, nextNs);
                    restoreValue = new DtlValue(t.Value, t.ValueNanosecond);
                    t.Value = next;
                    t.ValueNanosecond = nextNs;
                    return true;
                default:
                    return false;
            }
        }

        private static object GetTagValue(PlcTag tag)
        {
            switch (tag)
            {
                case PlcTagBool t: return t.Value;
                case PlcTagByte t: return t.Value;
                case PlcTagUSInt t: return t.Value;
                case PlcTagSInt t: return t.Value;
                case PlcTagWord t: return t.Value;
                case PlcTagUInt t: return t.Value;
                case PlcTagInt t: return t.Value;
                case PlcTagDWord t: return t.Value;
                case PlcTagUDInt t: return t.Value;
                case PlcTagDInt t: return t.Value;
                case PlcTagLWord t: return t.Value;
                case PlcTagULInt t: return t.Value;
                case PlcTagLInt t: return t.Value;
                case PlcTagReal t: return t.Value;
                case PlcTagLReal t: return t.Value;
                case PlcTagChar t: return t.Value;
                case PlcTagWChar t: return t.Value;
                case PlcTagString t: return t.Value;
                case PlcTagWString t: return t.Value;
                case PlcTagTime t: return t.Value;
                case PlcTagLTime t: return t.Value;
                case PlcTagDate t: return t.Value;
                case PlcTagTimeOfDay t: return new TodValue(t.Value);
                case PlcTagLTOD t: return new LTodValue(t.Value);
                case PlcTagDTL t: return new DtlValue(t.Value, t.ValueNanosecond);
                default: return null;
            }
        }

        private static void SetTagValue(PlcTag tag, object value)
        {
            switch (tag)
            {
                case PlcTagBool t: t.Value = (bool)value; break;
                case PlcTagByte t: t.Value = (byte)value; break;
                case PlcTagUSInt t: t.Value = (byte)value; break;
                case PlcTagSInt t: t.Value = (sbyte)value; break;
                case PlcTagWord t: t.Value = (ushort)value; break;
                case PlcTagUInt t: t.Value = (ushort)value; break;
                case PlcTagInt t: t.Value = (short)value; break;
                case PlcTagDWord t: t.Value = (uint)value; break;
                case PlcTagUDInt t: t.Value = (uint)value; break;
                case PlcTagDInt t: t.Value = (int)value; break;
                case PlcTagLWord t: t.Value = (ulong)value; break;
                case PlcTagULInt t: t.Value = (ulong)value; break;
                case PlcTagLInt t: t.Value = (long)value; break;
                case PlcTagReal t: t.Value = (float)value; break;
                case PlcTagLReal t: t.Value = (double)value; break;
                case PlcTagChar t: t.Value = (char)value; break;
                case PlcTagWChar t: t.Value = (char)value; break;
                case PlcTagString t: t.Value = (string)value; break;
                case PlcTagWString t: t.Value = (string)value; break;
                case PlcTagTime t: t.Value = (int)value; break;
                case PlcTagLTime t: t.Value = (long)value; break;
                case PlcTagDate t: t.Value = (DateTime)value; break;
                case PlcTagTimeOfDay t: t.Value = ((TodValue)value).Milliseconds; break;
                case PlcTagLTOD t: t.Value = ((LTodValue)value).Nanoseconds; break;
                case PlcTagDTL t:
                    var dtl = (DtlValue)value;
                    t.Value = dtl.Value;
                    t.ValueNanosecond = dtl.Nanosecond;
                    break;
            }
        }

        private static bool ValuesEqual(object lhs, object rhs)
        {
            if (lhs is float lf && rhs is float rf)
            {
                return Math.Abs(lf - rf) < 1e-5f;
            }

            if (lhs is double ld && rhs is double rd)
            {
                return Math.Abs(ld - rd) < 1e-9;
            }

            if (lhs is DateTime ldt && rhs is DateTime rdt)
            {
                return ldt == rdt;
            }

            if (lhs is DtlValue ldtl && rhs is DtlValue rdtl)
            {
                return ldtl.Value == rdtl.Value && ldtl.Nanosecond == rdtl.Nanosecond;
            }

            if (lhs is TodValue ltodMs && rhs is TodValue rtodMs)
            {
                return ltodMs.Milliseconds == rtodMs.Milliseconds;
            }

            if (lhs is LTodValue lltodNs && rhs is LTodValue rltodNs)
            {
                return lltodNs.Nanoseconds == rltodNs.Nanoseconds;
            }

            return Equals(lhs, rhs);
        }

        private static string ValueToText(object value)
        {
            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }

            if (value is TodValue todMs)
            {
                return todMs.Milliseconds + " (ms since 00:00:00)";
            }

            if (value is LTodValue ltodNs)
            {
                return ltodNs.Nanoseconds + " (ns since 00:00:00)";
            }

            if (value is DtlValue dtl)
            {
                return dtl.Value.ToString("yyyy-MM-dd HH:mm:ss") + "." + dtl.Nanosecond.ToString("D9");
            }

            return value == null ? "<null>" : value.ToString();
        }
    }
}
