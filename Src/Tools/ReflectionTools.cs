using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that reach into other mods by reflection: read or write a field or property of a loaded
    /// MonoBehaviour (or a static one), and call a method; a window's position, for one, is a field.
    /// </summary>
    internal static class ReflectionTools
    {
        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        private const string MatchDescription =
            "path=value: take the first loaded object whose path (as in get_member) reads this value, instead of " +
            "the first loaded object";

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("get_member",
                "Reads a field or property of the first loaded object of a type (its full or short name; the full " +
                "name for a type of Unity), or a static one, or follows a path from it: names of fields and " +
                "properties, indexes in a list, and [Type] for the components of that type on a Unity object and " +
                "its children, separated by dots (objects.0.[UnityEngine.Renderer].0.bounds). Numbers, strings, " +
                "booleans, vectors, rotations, bounds and rects come back as JSON; lists as their items. With " +
                "members, reads several paths from the same object in the same frame.",
                Schema.Object(
                    Schema.P("type", "string", "type name, full or short", true),
                    Schema.P("member", "string", "field or property name, or a path"),
                    Schema.P("members", "array",
                        "several paths instead of member, read in the same frame: the answer gives each one's value " +
                        "under its path, and why it could not be read under errors"),
                    Schema.P("match", "string", MatchDescription)),
                GetMember) { Concurrent = true };
            yield return new Tool("sample_members",
                "Reads a list of members at every rendered frame, after it is drawn, or at every physics step, " +
                "for a number of samples or seconds, and answers with a table: one row per sample, with the frame, " +
                "the real time since the first sample, the universal time, and each member's value (vectors and " +
                "rotations as arrays). All the members of a row are read in the same frame; one sample reads " +
                "members of different objects at once. Each object is found once, at the start, as get_member " +
                "finds it. Stops at " + SampleMaxRows + " rows, or once the table reaches " + SampleMaxChars / 1000 +
                " thousand characters, and says so.",
                Schema.Object(
                    Schema.P("reads", "array",
                        "the members to read, each an object {type, member, match, label}: type, member and match as " +
                        "get_member takes them, label the column's name (default type.member)", true),
                    Schema.P("frames", "number", "how many samples (default 100, at most " + SampleMaxRows + ")"),
                    Schema.P("seconds", "number",
                        "seconds of real time to sample for at most (default 60, at most 600): sampling stops at " +
                        "whichever of frames and seconds comes first"),
                    Schema.P("every", "string",
                        "frame: at the end of every rendered frame (default); physics: after every physics step, " +
                        "none while the game is paused"),
                    Schema.P("interval", "number", "keeps one frame or step out of this many (default 1)")),
                SampleMembers) { Concurrent = true };
            yield return new Tool("set_member",
                "Writes a field or property of the first loaded object of a type, or a static one. The value is a " +
                "number, string or boolean, or an object {x, y, width, height} for a Rect, {x, y, z} for a vector.",
                Schema.Object(
                    Schema.P("type", "string", "type name, full or short", true),
                    Schema.P("member", "string", "field or property name", true),
                    Schema.P("value", "object", "the new value", true),
                    Schema.P("match", "string", MatchDescription)),
                SetMember) { Concurrent = true };
            yield return new Tool("call_method",
                "Calls a method of the first loaded object of a type, or a static one, and returns what it " +
                "returns. Without arguments, the method that takes none; with arguments, the first method of that " +
                "name that takes as many, each argument converted to its parameter's type (numbers, strings, " +
                "booleans, enums, and {x, y, z} for a Vector3).",
                Schema.Object(
                    Schema.P("type", "string", "type name, full or short", true),
                    Schema.P("method", "string", "method name", true),
                    Schema.P("arguments", "array", "the arguments, in order"),
                    Schema.P("match", "string", MatchDescription)),
                CallMethod) { Concurrent = true };
        }

        // ----- generic reflection -----

        private static Type FindType(string name)
        {
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try
                {
                    types = loaded.assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                Type match = types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name);
                if (match != null)
                {
                    return match;
                }
            }
            Type game = typeof(Vessel).Assembly.GetType(name) ?? typeof(Vessel).Assembly.GetTypes().FirstOrDefault(t => t.Name == name);
            if (game != null)
            {
                return game;
            }
            // Unity's own types (UnityEngine.Renderer) are in none of the assemblies above: by full name only.
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        }

        /// <summary>Splits a path at its dots, except the dots inside a [Type] step.</summary>
        private static List<string> SplitPath(string path)
        {
            List<string> steps = new List<string>();
            int start = 0;
            int brackets = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '[')
                {
                    brackets++;
                }
                else if (path[i] == ']')
                {
                    brackets--;
                }
                else if (path[i] == '.' && brackets == 0)
                {
                    steps.Add(path.Substring(start, i - start));
                    start = i + 1;
                }
            }
            steps.Add(path.Substring(start));
            return steps;
        }

        /// <summary>The field or property of a type with this name, static or not, public or not; null if none.</summary>
        private static MemberInfo FindMember(Type type, string name)
        {
            return type.GetMember(name, AnyMember).FirstOrDefault(m => m is FieldInfo || m is PropertyInfo);
        }

        /// <summary>
        /// Follows a path from an object of a type (null for a path that starts with a static member) and gives the
        /// value it ends on, or false and why it could not.
        /// </summary>
        private static bool ReadPath(Type type, object target, List<string> path, out object value, out string error)
        {
            value = target;
            error = null;
            Type current = type;
            for (int i = 0; i < path.Count; i++)
            {
                string step = path[i];
                // Only the first step may start from nothing: a static member.
                if (value == null && i > 0)
                {
                    error = "null before " + step;
                    return false;
                }
                if (step.StartsWith("[") && step.EndsWith("]"))
                {
                    // [Type]: the components of that type on a GameObject or a component's GameObject, children included.
                    Type componentType = FindType(step.Substring(1, step.Length - 2));
                    GameObject gameObject = value is GameObject g ? g : (value as Component)?.gameObject;
                    if (componentType == null || gameObject == null)
                    {
                        error = componentType == null ? "Unknown type " + step : step + " needs a Unity object, not " + current.Name;
                        return false;
                    }
                    value = gameObject.GetComponentsInChildren(componentType, true);
                }
                else if (int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    if (!(value is IEnumerable items))
                    {
                        error = step + " is an index, and " + current.Name + " is not a list";
                        return false;
                    }
                    List<object> list = items.Cast<object>().ToList();
                    if (index < 0 || index >= list.Count)
                    {
                        error = "index " + step + " out of a list of " + list.Count;
                        return false;
                    }
                    value = list[index];
                }
                else
                {
                    MemberInfo member = FindMember(current, step);
                    if (member == null)
                    {
                        error = "No member " + step + " in " + current.FullName;
                        return false;
                    }
                    value = member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value, null);
                }
                current = value?.GetType() ?? typeof(object);
            }
            return true;
        }

        // The object a member is read on: null for a static member, the first loaded instance otherwise.
        private static bool Target(ToolCall call, Type type, MemberInfo member, out object target)
        {
            string error;
            if (!FindTarget(type, member, call.String("match"), out target, out error))
            {
                call.Fail(error);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The object a member of a type is read on: null for a static member; for another, the first loaded object
        /// of the type, or with <paramref name="match"/> (path=value, or null) the first whose path reads that value.
        /// Returns false with <paramref name="error"/> set when there is none.
        /// </summary>
        private static bool FindTarget(Type type, MemberInfo member, string match, out object target, out string error)
        {
            target = null;
            error = null;
            bool isStatic = member is FieldInfo f ? f.IsStatic
                : member is PropertyInfo p ? (p.GetGetMethod(true) ?? p.GetSetMethod(true)).IsStatic
                : member is MethodInfo m && m.IsStatic;
            if (isStatic)
            {
                return true;
            }
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                error = type.FullName + "." + member.Name + " is not static, and " + type.Name + " is not a Unity object to look for";
                return false;
            }
            if (match == null)
            {
                target = UnityEngine.Object.FindObjectOfType(type);
                if (target == null)
                {
                    error = "No loaded " + type.FullName;
                    return false;
                }
                return true;
            }
            int equals = match.IndexOf('=');
            if (equals <= 0)
            {
                error = "match is path=value, not " + match;
                return false;
            }
            List<string> path = SplitPath(match.Substring(0, equals));
            string expected = match.Substring(equals + 1);
            foreach (UnityEngine.Object candidate in UnityEngine.Object.FindObjectsOfType(type))
            {
                if (ReadPath(type, candidate, path, out object read, out string _)
                    && Convert.ToString(read, CultureInfo.InvariantCulture) == expected)
                {
                    target = candidate;
                    return true;
                }
            }
            error = "No loaded " + type.FullName + " whose " + match;
            return false;
        }

        /// <summary>
        /// A path of members to read, from its type's name: the type, the object its first step is read on (null
        /// for a static member) and its steps. Returns false with <paramref name="error"/> set when the type, the
        /// first member or the object cannot be found.
        /// </summary>
        private static bool Resolve(string typeName, string member, string match, out Type type, out object target,
            out List<string> path, out string error)
        {
            target = null;
            path = null;
            type = typeName != null ? FindType(typeName) : null;
            if (type == null)
            {
                error = "Unknown type " + typeName;
                return false;
            }
            if (string.IsNullOrEmpty(member))
            {
                error = "No member given for " + type.FullName;
                return false;
            }
            path = SplitPath(member);
            // The first step tells whether the path starts on a static member or on a loaded object.
            MemberInfo first = FindMember(type, path[0]);
            if (first == null)
            {
                error = "No member " + path[0] + " in " + type.FullName;
                return false;
            }
            return FindTarget(type, first, match, out target, out error);
        }

        /// <summary>
        /// Reads a path from its object, as data ready to be written as JSON, to a depth (compact, as a table cell,
        /// when negative); false with the reason when it cannot, a member that throws included.
        /// </summary>
        private static bool Read(Type type, object target, List<string> path, int depth, out object value, out string error)
        {
            // A Unity object destroyed since it was found compares equal to null.
            if (target is UnityEngine.Object unity && unity == null)
            {
                value = null;
                error = "the " + type.Name + " was destroyed";
                return false;
            }
            try
            {
                if (!ReadPath(type, target, path, out object read, out error))
                {
                    value = null;
                    return false;
                }
                value = depth < 0 ? Compact(read) : ToJson(read, depth);
                return true;
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                value = null;
                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private static IEnumerator GetMember(ToolCall call)
        {
            List<object> several = call.Arguments.TryGetValue("members", out object given) ? given as List<object> : null;
            if (several == null)
            {
                if (!Resolve(call.String("type"), call.String("member"), call.String("match"), out Type type,
                    out object target, out List<string> path, out string error)
                    || !Read(type, target, path, 2, out object value, out error))
                {
                    call.Fail(error);
                    yield break;
                }
                call.Text(new Dictionary<string, object> { { "value", value } });
                yield break;
            }
            if (call.Has("member"))
            {
                call.Fail("Give member or members, not both");
                yield break;
            }
            // Every path is read before the coroutine yields: all in the same frame.
            Dictionary<string, object> values = new Dictionary<string, object>();
            Dictionary<string, object> errors = new Dictionary<string, object>();
            foreach (object item in several)
            {
                string member = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (Resolve(call.String("type"), member, call.String("match"), out Type type, out object target,
                    out List<string> path, out string error)
                    && Read(type, target, path, 2, out object value, out error))
                {
                    values[member] = value;
                }
                else
                {
                    errors[member] = error;
                }
            }
            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "frame", Time.frameCount },
                { "values", values }
            };
            if (errors.Count > 0)
            {
                answer["errors"] = errors;
            }
            // Failed when not one path could be read.
            call.IsError = values.Count == 0;
            call.Text(answer);
        }

        // sample_members: the most rows it answers with, and the most characters its table may take.
        private const int SampleMaxRows = 5000;
        private const int SampleMaxChars = 200000;

        /// <summary>One column of sample_members: a path, the object it is read on, and its label.</summary>
        private sealed class Column
        {
            public string Label;
            public Type Type;
            public object Target;
            public List<string> Path;
        }

        private static IEnumerator SampleMembers(ToolCall call)
        {
            List<object> reads = call.Arguments.TryGetValue("reads", out object given) ? given as List<object> : null;
            if (reads == null || reads.Count == 0)
            {
                call.Fail("reads: a list of {type, member, match, label}");
                yield break;
            }
            bool physics;
            if (!call.Choice("every", "frame", new Dictionary<string, bool> { { "frame", false }, { "physics", true } },
                out physics))
            {
                yield break;
            }
            int frames = (int)Math.Min(call.Number("frames", 100.0), SampleMaxRows);
            double seconds = Math.Min(call.Number("seconds", 60.0), 600.0);
            int interval = Math.Max(1, (int)call.Number("interval", 1.0));
            if (frames < 1 || !(seconds > 0.0))
            {
                call.Fail("frames and seconds: more than 0");
                yield break;
            }

            // Each object is found once: a column whose object is destroyed meanwhile reads an error.
            List<Column> columns = new List<Column>();
            List<object> header = new List<object> { "frame", "time", "ut" };
            foreach (object read in reads)
            {
                Dictionary<string, object> spec = read as Dictionary<string, object>;
                if (spec == null)
                {
                    call.Fail("reads: each item is an object {type, member, match, label}, not " + Json.Write(read));
                    yield break;
                }
                string typeName = spec.TryGetValue("type", out object t) ? t as string : null;
                string member = spec.TryGetValue("member", out object m) ? m as string : null;
                string match = spec.TryGetValue("match", out object w) ? w as string : null;
                if (!Resolve(typeName, member, match, out Type type, out object target, out List<string> path,
                    out string error))
                {
                    call.Fail(typeName + "." + member + ": " + error);
                    yield break;
                }
                string label = spec.TryGetValue("label", out object l) && l is string named ? named : typeName + "." + member;
                columns.Add(new Column { Label = label, Type = type, Target = target, Path = path });
                header.Add(label);
            }

            List<object> rows = new List<object>();
            int characters = Json.Write(header).Length;
            bool truncated = false;
            // Real time of the first sample; the time limit counts from the start of the call.
            float start = -1f;
            float called = Time.realtimeSinceStartup;
            int counted = 0;
            while (rows.Count < frames && Time.realtimeSinceStartup - called <= seconds)
            {
                if (physics && Time.timeScale == 0f)
                {
                    // While the game is paused no physics step comes: waited for frame by frame, so that the time
                    // limit still ends the call.
                    yield return null;
                    continue;
                }
                if (physics)
                {
                    yield return new WaitForFixedUpdate();
                }
                else
                {
                    yield return new WaitForEndOfFrame();
                }
                if (counted++ % interval != 0)
                {
                    continue;
                }
                if (start < 0f)
                {
                    start = Time.realtimeSinceStartup;
                }
                List<object> row = new List<object>
                {
                    Time.frameCount,
                    (double)(Time.realtimeSinceStartup - start),
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : double.NaN
                };
                foreach (Column column in columns)
                {
                    row.Add(Read(column.Type, column.Target, column.Path, -1, out object value, out string error)
                        ? value : "error: " + error);
                }
                characters += Json.Write(row).Length + 1;
                if (characters > SampleMaxChars)
                {
                    truncated = true;
                    break;
                }
                rows.Add(row);
            }
            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "every", physics ? "physics" : "frame" },
                { "columns", header },
                { "rows", rows }
            };
            if (truncated || rows.Count == SampleMaxRows)
            {
                answer["truncated"] = true;
            }
            call.Text(answer);
        }

        /// <summary>
        /// A value as compact JSON-ready data, for a table: vectors and rotations as arrays of their components,
        /// anything else as get_member writes it, one level deep.
        /// </summary>
        private static object Compact(object value)
        {
            if (value is Vector3 v3)
            {
                return new List<object> { (double)v3.x, (double)v3.y, (double)v3.z };
            }
            if (value is Vector3d vd)
            {
                return new List<object> { vd.x, vd.y, vd.z };
            }
            if (value is Vector2 v2)
            {
                return new List<object> { (double)v2.x, (double)v2.y };
            }
            if (value is Vector2d v2d)
            {
                return new List<object> { v2d.x, v2d.y };
            }
            if (value is Quaternion q)
            {
                return new List<object> { (double)q.x, (double)q.y, (double)q.z, (double)q.w };
            }
            if (value is QuaternionD qd)
            {
                return new List<object> { qd.x, qd.y, qd.z, qd.w };
            }
            return ToJson(value, 1);
        }

        private static IEnumerator SetMember(ToolCall call)
        {
            Type type = FindType(call.String("type"));
            if (type == null)
            {
                call.Fail("Unknown type " + call.String("type"));
                yield break;
            }
            MemberInfo member = type.GetMember(call.String("member"), AnyMember).FirstOrDefault();
            object target;
            if (member == null)
            {
                call.Fail("No member " + call.String("member") + " in " + type.FullName);
                yield break;
            }
            if (!Target(call, type, member, out target))
            {
                yield break;
            }
            Type valueType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
            object value = FromJson(call.Arguments["value"], valueType);
            if (member is FieldInfo field)
            {
                field.SetValue(target, value);
            }
            else
            {
                ((PropertyInfo)member).SetValue(target, value, null);
            }
            call.Text(new Dictionary<string, object> { { "value", ToJson(value, 2) } });
        }

        private static IEnumerator CallMethod(ToolCall call)
        {
            Type type = FindType(call.String("type"));
            if (type == null)
            {
                call.Fail("Unknown type " + call.String("type"));
                yield break;
            }
            List<object> arguments = call.Arguments.TryGetValue("arguments", out object given) && given is List<object> list
                ? list
                : new List<object>();
            MethodInfo method = type.GetMethods(AnyMember)
                .FirstOrDefault(m => m.Name == call.String("method") && m.GetParameters().Length == arguments.Count);
            object target;
            if (method == null)
            {
                call.Fail("No method " + call.String("method") + " with " + arguments.Count + " parameters in " + type.FullName);
                yield break;
            }
            if (!Target(call, type, method, out target))
            {
                yield break;
            }
            ParameterInfo[] parameters = method.GetParameters();
            object[] values = new object[arguments.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = FromJson(arguments[i], parameters[i].ParameterType);
            }
            object result = method.Invoke(target, values);
            call.Text(new Dictionary<string, object> { { "returned", ToJson(result, 2) } });
        }

        /// <summary>A value as JSON-ready data; objects of other types show their public fields, to a depth.</summary>
        public static object ToJson(object value, int depth)
        {
            if (value == null || value is string || value is bool || value is double || value is float
                || value is int || value is long || value is short || value is byte || value is uint)
            {
                return value;
            }
            if (value is Enum)
            {
                return value.ToString();
            }
            if (value is Vector3 v3)
            {
                return new Dictionary<string, object> { { "x", (double)v3.x }, { "y", (double)v3.y }, { "z", (double)v3.z } };
            }
            if (value is Vector3d vd)
            {
                return new Dictionary<string, object> { { "x", vd.x }, { "y", vd.y }, { "z", vd.z } };
            }
            if (value is Quaternion q)
            {
                return new Dictionary<string, object> { { "x", (double)q.x }, { "y", (double)q.y }, { "z", (double)q.z }, { "w", (double)q.w } };
            }
            if (value is Bounds b)
            {
                return new Dictionary<string, object> { { "center", ToJson(b.center, depth) }, { "size", ToJson(b.size, depth) } };
            }
            if (value is Rect r)
            {
                return new Dictionary<string, object>
                {
                    { "x", (double)r.x }, { "y", (double)r.y }, { "width", (double)r.width }, { "height", (double)r.height }
                };
            }
            if (value is UnityEngine.Object unity)
            {
                return unity.ToString();
            }
            if (value is IDictionary dictionary)
            {
                if (depth <= 0)
                {
                    return "{...}";
                }
                Dictionary<string, object> entries = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = ToJson(entry.Value, depth - 1);
                }
                return entries;
            }
            if (value is IEnumerable list)
            {
                return depth <= 0 ? (object)"[...]" : list.Cast<object>().Select(o => ToJson(o, depth - 1)).ToList();
            }
            if (depth <= 0)
            {
                return value.ToString();
            }
            Dictionary<string, object> fields = new Dictionary<string, object>();
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                fields[field.Name] = ToJson(field.GetValue(value), depth - 1);
            }
            return fields;
        }

        private static object FromJson(object json, Type type)
        {
            if (type == typeof(Rect))
            {
                Dictionary<string, object> d = (Dictionary<string, object>)json;
                return new Rect(F(d, "x"), F(d, "y"), F(d, "width"), F(d, "height"));
            }
            if (type == typeof(Vector3))
            {
                Dictionary<string, object> d = (Dictionary<string, object>)json;
                return new Vector3(F(d, "x"), F(d, "y"), F(d, "z"));
            }
            if (type.IsEnum)
            {
                return Enum.Parse(type, Convert.ToString(json, CultureInfo.InvariantCulture));
            }
            return Convert.ChangeType(json, type, CultureInfo.InvariantCulture);
        }

        private static float F(Dictionary<string, object> d, string key)
        {
            return d.ContainsKey(key) ? Convert.ToSingle(d[key], CultureInfo.InvariantCulture) : 0f;
        }
    }
}
