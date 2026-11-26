using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The sandbox setting: when on, the server keeps to the game, rather than acting on the rest of the computer
    /// with the rights of whoever runs KSP. Types are looked up among the assemblies of the game, of the mods and of
    /// Unity only; set_member and call_method write or pass numbers, booleans, enums and vectors only; every file a
    /// tool reads or writes lies inside the KSP folder. Set once, when KSP starts.
    /// </summary>
    /// <remarks>
    /// Leaving the game takes a string chosen by the caller: a path, a URL, a command. Setting
    /// MainMenu.KSPsiteURL then calling MainMenu.goToKSPSite starts any program, and so does
    /// PrivacyDialog.OnPrivacyURLReceived; no list of members would catch those of every mod, a ban on strings
    /// does. A guard against mistakes and injected instructions, not a proof: a method without parameters still
    /// runs whatever it runs.
    /// </remarks>
    internal static class Sandbox
    {
        /// <summary>The types a sandboxed set_member or call_method converts, besides numbers, booleans and enums.</summary>
        private static readonly Type[] Structures = { typeof(Rect), typeof(Vector3), typeof(Vector3d) };

        /// <summary>Whether the sandbox is on.</summary>
        public static bool On { get; private set; }

        /// <summary>Turns the sandbox on or off; called once, before the server listens.</summary>
        public static void Set(bool on)
        {
            On = on;
        }

        /// <summary>
        /// Whether a tool may read or write this file: always outside the sandbox, inside it only when the file
        /// lies in the KSP folder. Fails the call with the reason when it may not. A relative path is taken from
        /// the current directory: callers resolve theirs first.
        /// </summary>
        public static bool Allows(ToolCall call, string path)
        {
            if (!On)
            {
                return true;
            }
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception e)
            {
                call.Fail("Invalid path " + path + ": " + e.Message);
                return false;
            }
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            call.Fail("The server is sandboxed: " + full + " is outside the KSP folder " + root);
            return false;
        }

        /// <summary>
        /// Whether the types of an assembly outside the game's and the mods' may be looked up: always outside the
        /// sandbox, inside it Unity's only, never the framework's (System.IO.File, System.Diagnostics.Process).
        /// </summary>
        public static bool Allows(Assembly assembly)
        {
            return !On || assembly.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether set_member or call_method may convert a value to this type: always outside the sandbox, inside
        /// it a number, a boolean, an enum or one of <see cref="Structures"/>, never a string, an object or a
        /// pointer. Fails the call with the reason when it may not; <paramref name="what"/> names the member.
        /// </summary>
        public static bool AllowsValue(ToolCall call, Type type, string what)
        {
            if (!On || type.IsEnum || Array.IndexOf(Structures, type) >= 0
                || (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr)))
            {
                return true;
            }
            call.Fail("The server is sandboxed: " + what + " takes a " + type.Name +
                ", and only numbers, booleans, enums and vectors are written or passed");
            return false;
        }
    }
}
