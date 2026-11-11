# Only the registry boundary is replaced; production validation remains active.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using Microsoft.Win32;
namespace ServerSuiteRegistryFixture {
    public sealed class Node {
        public readonly Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    public static class Registry {
        static Dictionary<string, Node> roots = new Dictionary<string, Node>();
        public static List<string> Reads = new List<string>(), Writes = new List<string>();
        public static string Denied = "";
        public static int Disposed;
        public static void Reset() { roots.Clear(); Reads.Clear(); Writes.Clear(); Denied=""; Disposed=0; }
        public static Key Open(string view) {
            Reads.Add(view);
            if(view==Denied) throw new UnauthorizedAccessException("simulated registry access denied");
            Node root;
            if(!roots.TryGetValue(view,out root)) { root=new Node(); roots.Add(view,root); }
            return new Key(view,"",root,true);
        }
        public static void Seed(string view,string path,string name,string value) {
            using(var root=Open(view)) using(var key=root.CreateSubKey(path)) key.SetValue(name,value,RegistryValueKind.String);
            Reads.Clear(); Writes.Clear(); Disposed=0;
        }
    }
    public sealed class Key : IDisposable {
        readonly string view,path; readonly Node node; readonly bool writable;
        public Key(string view,string path,Node node,bool writable) { this.view=view; this.path=path; this.node=node; this.writable=writable; }
        public string[] GetSubKeyNames() { return new List<string>(node.Children.Keys).ToArray(); }
        public object GetValue(string name, object fallback) { string value; return node.Values.TryGetValue(name,out value) ? value : fallback; }
        Key Child(string subpath,bool write,bool create) {
            Node next=node;
            foreach(var part in subpath.Split('\\')) {
                Node child;
                if(!next.Children.TryGetValue(part,out child)) {
                    if(!create) return null;
                    child=new Node(); next.Children.Add(part,child);
                }
                next=child;
            }
            return new Key(view,path.Length==0 ? subpath : path+"\\"+subpath,next,write);
        }
        public Key OpenSubKey(string subpath,bool write) { Registry.Reads.Add(view+"/"+subpath+"/"+write); return Child(subpath,write,false); }
        public Key CreateSubKey(string subpath) { return Child(subpath,true,true); }
        public void SetValue(string name, object value, RegistryValueKind kind) {
            if(!writable) throw new Exception("write through a read-only registry handle");
            node.Values[name]=Convert.ToString(value); Registry.Writes.Add(view+"/"+path+"/"+name);
        }
        public void Dispose() { Registry.Disposed++; }
    }
}
'@
function ReplaceSuiteRegistryAccess([string]$Code) {
    $parseErrors=$null
    $registryAst=[Management.Automation.Language.Parser]::ParseInput($Code,[ref]$null,[ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    $definitions=$registryAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'OpenSuiteRegistry'},$true)
    foreach ($definition in @($definitions | Sort-Object { $_.Extent.StartOffset } -Descending)) {
        $Code=$Code.Remove($definition.Extent.StartOffset,$definition.Extent.EndOffset-$definition.Extent.StartOffset).Insert($definition.Extent.StartOffset,
            'function OpenSuiteRegistry([string]$view) { [ServerSuiteRegistryFixture.Registry]::Open($view) }')
    }
    return $Code
}
