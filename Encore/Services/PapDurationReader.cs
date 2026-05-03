using System;
using System.IO;
using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace Encore.Services;

// Reads PAP animation loop duration via embedded havok section.
// MUST run on framework (main) thread: havok runtime is not thread-safe.
// PAP: magic"pap " ver numAnim modelId modelType variant infoOffset(0x0E) havokOffset(0x12) footerOffset(0x16)
public static unsafe class PapDurationReader
{
    public static float? ReadFromPapBytes(byte[] papBytes)
    {
        if (papBytes == null || papBytes.Length < 0x1A) return null;
        if (papBytes[0] != 0x70 || papBytes[1] != 0x61 || papBytes[2] != 0x70) return null;

        int havokStart = BitConverter.ToInt32(papBytes, 0x12);
        int footerStart = BitConverter.ToInt32(papBytes, 0x16);
        if (havokStart <= 0 || footerStart <= havokStart || footerStart > papBytes.Length) return null;

        int havokSize = footerStart - havokStart;
        if (havokSize < 16) return null;

        var havokBytes = new byte[havokSize];
        Array.Copy(papBytes, havokStart, havokBytes, 0, havokSize);

        var tmpPath = Path.Combine(Path.GetTempPath(), $"encore_dur_{Guid.NewGuid():N}.hkx");
        try
        {
            File.WriteAllBytes(tmpPath, havokBytes);
            return LoadDurationFromFile(tmpPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    private static float? LoadDurationFromFile(string path)
    {
        var pathPtr = Marshal.StringToHGlobalAnsi(path);
        hkResource* resource = null;
        try
        {
            var loadOptions = stackalloc hkSerializeUtil.LoadOptions[1];
            loadOptions->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
            loadOptions->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
            loadOptions->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
            {
                Storage = (int)hkSerializeUtil.LoadOptionBits.Default,
            };

            resource = hkSerializeUtil.LoadFromFile((byte*)pathPtr, null, loadOptions);
            if (resource == null) return null;

            var rootName = "hkRootLevelContainer"u8;
            var animName = "hkaAnimationContainer"u8;
            fixed (byte* n1 = rootName)
            fixed (byte* n2 = animName)
            {
                var typeRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
                var container = (hkRootLevelContainer*)resource->GetContentsPointer(n1, typeRegistry);
                if (container == null) return null;

                var animContainer = (hkaAnimationContainer*)container->findObjectByName(n2, null);
                if (animContainer == null) return null;

                if (animContainer->Bindings.Length == 0) return null;
                var binding = animContainer->Bindings[0].ptr;
                if (binding == null) return null;
                var anim = binding->Animation.ptr;
                if (anim == null) return null;

                var dur = anim->Duration;
                return dur > 0 && dur < 600f ? dur : null;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (resource != null)
            {
                try
                {
                    var refObj = (hkReferencedObject*)resource;
                    refObj->RemoveReference();
                }
                catch { }
            }
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
