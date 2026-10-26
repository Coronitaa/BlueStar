using System;
using System.Runtime.InteropServices;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Authenticode check through WinVerifyTrust, the same verdict Windows shows in a file's Digital Signatures tab
/// (timestamped signatures stay valid after the signing certificate expires).
/// </summary>
internal static class WinTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WintrustData pWVTData);

    public static bool IsSignatureTrusted(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path
        };

        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, fDeleteOld: false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = filePtr,
                dwStateAction = StateActionVerify
            };

            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

            data.dwStateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
