// ApiDump: lists the public API of one namespace in an assembly using MetadataLoadContext (no loading into the process).
// Usage: dotnet run -- <path to Microsoft.VisualStudio.Language.dll> [namespace | *list*] [all]
// Dependencies are resolved from the NuGet cache (prefers 17.14 packages). Pairs with the assembly's .xml docs when present.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
var target = args[0];
var ns = args.Length > 1 ? args[1] : "Microsoft.VisualStudio.Language.Proposals";
var pkgRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
// Add net472 / netstandard dlls from all VS packages in cache (latest matching 17.14 where possible)
foreach (var d in Directory.GetDirectories(pkgRoot, "microsoft.visualstudio.*").Concat(Directory.GetDirectories(pkgRoot, "system.*")).Concat(Directory.GetDirectories(pkgRoot, "microsoft.servicehub*")).Concat(Directory.GetDirectories(pkgRoot, "streamjsonrpc")))
{
  var ver = Directory.GetDirectories(d).OrderByDescending(x => x).FirstOrDefault(v => Path.GetFileName(v).StartsWith("17.14")) ?? Directory.GetDirectories(d).OrderByDescending(x=>x).FirstOrDefault();
  if (ver == null) continue;
  foreach (var tfm in new[]{"lib/net472","lib/netstandard2.0","lib/net46","lib/net45"}) {
    var p = Path.Combine(ver, tfm); if (Directory.Exists(p)) { paths.AddRange(Directory.GetFiles(p, "*.dll")); break; }
  }
}
paths.Add(target);
var byName = paths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
var resolver = new PathAssemblyResolver(byName);
using var mlc = new MetadataLoadContext(resolver, "System.Private.CoreLib");
var asm = mlc.LoadFromAssemblyPath(target);
Console.WriteLine($"# {asm.FullName}");
var xmlPath = Path.ChangeExtension(target, ".xml");
var docs = File.Exists(xmlPath) ? XDocument.Load(xmlPath).Descendants("member").ToDictionary(m => (string)m.Attribute("name"), m => string.Join(" ", ((string)m.Element("summary") ?? "").Split(new[]{'\r','\n',' '}, StringSplitOptions.RemoveEmptyEntries))) : new();
string Doc(string k) => docs.TryGetValue(k, out var s) && s.Length > 0 ? "    /// " + (s.Length > 220 ? s[..220] + "..." : s) : null;
string TN(Type t) { try { if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TN)) + ">"; return t.Name; } catch { return "?"; } }
string Attrs(MemberInfo m) { try { var a = m.GetCustomAttributesData().Where(c => c.AttributeType.Name is "ObsoleteAttribute" or "EditorBrowsableAttribute" or "ExperimentalAttribute").Select(c => c.AttributeType.Name.Replace("Attribute", "") + (c.ConstructorArguments.Count > 0 ? "(" + string.Join(", ", c.ConstructorArguments.Select(x => x.Value is string s ? "\"" + s + "\"" : x.Value?.ToString())) + ")" : "")); var l = string.Join(",", a); return l.Length > 0 ? $" [{l}]" : ""; } catch { return ""; } }
Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
if (ns == "*list*") { foreach (var g in types.Where(t=>t.IsPublic).GroupBy(t => t.Namespace).OrderBy(g=>g.Key)) Console.WriteLine($"{g.Key}: {g.Count()} -- {string.Join(", ", g.Select(t=>TN(t)).Where(n => n.Contains("Suggest") || n.Contains("Propos") || n.Contains("Inline")))}"); return; }
foreach (var t in types.Where(t => t.Namespace == ns).OrderBy(t => t.Name))
{
  string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : (t.IsAbstract && t.IsSealed) ? "static class" : t.IsAbstract ? "abstract class" : t.IsSealed ? "sealed class" : "class";
  string vis = t.IsPublic || t.IsNestedPublic ? "public" : "internal";
  if (vis == "internal" && args.Length < 3) { Console.WriteLine($"\n## internal {kind} {TN(t)}"); continue; }
  string bases = ""; try { var b = new List<string>(); if (t.BaseType != null && t.BaseType.Name != "Object" && !t.IsEnum && !t.IsValueType) b.Add(TN(t.BaseType)); b.AddRange(t.GetInterfaces().Select(TN)); if (b.Count>0) bases = " : " + string.Join(", ", b);} catch {}
  Console.WriteLine($"\n## {vis} {kind} {TN(t)}{bases}{Attrs(t)}");
  var d = Doc("T:" + t.FullName); if (d != null) Console.WriteLine(d);
  if (t.IsEnum) { foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.Static)) Console.WriteLine($"  {f.Name} = {f.GetRawConstantValue()}{Attrs(f)}"); continue; }
  const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
  foreach (var c in t.GetConstructors(BF).Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly))
    Console.WriteLine($"  {(c.IsPublic?"public":"protected")} ctor({string.Join(", ", c.GetParameters().Select(p => TN(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? "=" + (p.RawDefaultValue ?? "null") : "")))}){Attrs(c)}");
  foreach (var p in t.GetProperties(BF)) {
    var g = p.GetGetMethod(true); var s = p.GetSetMethod(true);
    bool vis2 = (g != null && (g.IsPublic || g.IsFamily)) || (s != null && (s.IsPublic || s.IsFamily)); if (!vis2) continue;
    string mod = (g ?? s).IsStatic ? "static " : (g ?? s).IsAbstract ? "abstract " : (g ?? s).IsVirtual && !(g ?? s).IsFinal ? "virtual " : "";
    Console.WriteLine($"  {((g??s).IsPublic?"public":"protected")} {mod}{TN(p.PropertyType)} {p.Name} {{ {(g!=null && (g.IsPublic||g.IsFamily)?"get; ":"")}{(s!=null && (s.IsPublic||s.IsFamily)?"set; ":"")}}}{Attrs(p)}");
  }
  foreach (var m in t.GetMethods(BF).Where(m => !m.IsSpecialName && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))) {
    string mod = m.IsStatic ? "static " : m.IsAbstract ? "abstract " : m.IsVirtual && !m.IsFinal ? ((m.Attributes & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot ? "virtual " : "override ") : "";
    Console.WriteLine($"  {(m.IsPublic?"public":"protected")} {mod}{TN(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => TN(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? "=" + (p.RawDefaultValue ?? "null") : "")))}){Attrs(m)}");
  }
  foreach (var e in t.GetEvents(BF)) Console.WriteLine($"  event {TN(e.EventHandlerType)} {e.Name}");
  foreach (var f in t.GetFields(BF).Where(f => f.IsPublic || f.IsFamily)) Console.WriteLine($"  field {(f.IsStatic?"static ":"")}{TN(f.FieldType)} {f.Name}{(f.IsLiteral? " = " + f.GetRawConstantValue():"")}");
}
