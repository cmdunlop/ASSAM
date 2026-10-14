using System;
using System.Globalization;

namespace Assam.Core.Binary
{
    /// <summary>載入的二進位檔種類。</summary>
    public enum BinaryKind
    {
        /// <summary>32 位元 Windows PE（PE32）。</summary>
        Pe32,
        /// <summary>64 位元 Windows PE（PE32+）。</summary>
        Pe64,
        /// <summary>16 位元 DOS MZ 執行檔（MASM/TASM .model small 編出的 .EXE）。</summary>
        DosMz,
        /// <summary>16 位元 DOS .COM 檔（ORG 100h）。</summary>
        DosCom,
        /// <summary>沒有檔頭的原始機器碼。</summary>
        Raw
    }

    /// <summary>PE 區段（.text、.data、.rdata…），或 DOS/原始檔合成的單一區段。</summary>
    public sealed class PeSection
    {
        public string Name { get; set; }
        /// <summary>區段起始的虛擬位址（VA = ImageBase + RVA）。</summary>
        public ulong VirtualAddress { get; set; }
        public uint Rva { get; set; }
        public uint VirtualSize { get; set; }
        public uint RawOffset { get; set; }
        public uint RawSize { get; set; }
        public uint Characteristics { get; set; }

        public bool IsExecutable
        {
            get { return (Characteristics & 0x20000000u) != 0 || (Characteristics & 0x00000020u) != 0; }
        }

        public bool IsReadable
        {
            get { return (Characteristics & 0x40000000u) != 0; }
        }

        public bool IsWritable
        {
            get { return (Characteristics & 0x80000000u) != 0; }
        }

        /// <summary>區段在記憶體中的大小（VirtualSize 為 0 時用 RawSize）。</summary>
        public uint MemorySize
        {
            get { return VirtualSize != 0 ? VirtualSize : RawSize; }
        }

        public string AccessText
        {
            get { return (IsReadable ? "R" : "-") + (IsWritable ? "W" : "-") + (IsExecutable ? "X" : "-"); }
        }

        public bool Contains(ulong va)
        {
            return va >= VirtualAddress && va - VirtualAddress < Math.Max(MemorySize, RawSize);
        }

        public override string ToString()
        {
            return Name + "  VA=0x" + VirtualAddress.ToString("X", CultureInfo.InvariantCulture) + "  大小=0x" +
                   MemorySize.ToString("X", CultureInfo.InvariantCulture) + "  " + AccessText;
        }
    }

    /// <summary>匯入的函式（來自其他 DLL）。</summary>
    public sealed class PeImport
    {
        public string Dll { get; set; }
        /// <summary>函式名稱；以序號匯入時為 null。</summary>
        public string Name { get; set; }
        public int Ordinal { get; set; }
        /// <summary>匯入位址表（IAT）中此函式槽位的虛擬位址；程式以 call [IAT槽位] 間接呼叫。</summary>
        public ulong IatAddress { get; set; }
        public bool IsDelayLoad { get; set; }

        public string DisplayName
        {
            get { return Name ?? ("#" + Ordinal.ToString(CultureInfo.InvariantCulture)); }
        }

        public string FullName
        {
            get { return Dll + "!" + DisplayName; }
        }

        public override string ToString()
        {
            return FullName + (IsDelayLoad ? "（延遲載入）" : string.Empty);
        }
    }

    /// <summary>DLL 匯出的函式。</summary>
    public sealed class PeExport
    {
        /// <summary>名稱；只有序號時為 null。</summary>
        public string Name { get; set; }
        public int Ordinal { get; set; }
        public uint Rva { get; set; }
        public ulong Address { get; set; }
        /// <summary>轉送匯出（例如 "NTDLL.RtlAllocateHeap"），沒有轉送時為 null。</summary>
        public string ForwardTo { get; set; }

        public string DisplayName
        {
            get { return Name ?? ("#" + Ordinal.ToString(CultureInfo.InvariantCulture)); }
        }

        public override string ToString()
        {
            return DisplayName + (ForwardTo != null ? " → " + ForwardTo : "  0x" + Address.ToString("X", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>從檔案中找到的字串。</summary>
    public sealed class FoundString
    {
        public ulong Address { get; set; }
        public string Text { get; set; }
        /// <summary>"ASCII" 或 "UTF-16"。</summary>
        public string Encoding { get; set; }
        public string Section { get; set; }

        public override string ToString()
        {
            return "0x" + Address.ToString("X", CultureInfo.InvariantCulture) + "  " + (Encoding == "UTF-16" ? "L" : string.Empty) + "\"" + Text + "\"";
        }
    }

    /// <summary>檔案格式錯誤（不是 EXE/DLL、檔頭損毀、太大…）。</summary>
    public sealed class BinaryFormatException : Exception
    {
        public BinaryFormatException(string message) : base(message)
        {
        }
    }
}
