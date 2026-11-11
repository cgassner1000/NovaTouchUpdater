using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
namespace CompilationFixture {
    public static class State {
        public static List<string> Calls = new List<string>();
        public static List<string> CompiledItems = new List<string>();
        public static Dictionary<string, Record> Versions = new Dictionary<string, Record>();
        public static string Resource = "", FailResource = "", FailNamespace = "", FailItem = "", FailCompileResource = "", CompileErrorMessage = "";
        public static bool Locked, DenyLock, FailLoad, FailSave, Fallback, AlreadyCurrent, FailBatches, FailRewind, AllClassesCurrent, FailGlobals, DenyFileCopy, FailNativeWrite, TruncateNativeWrite;
        public static int Disposed, CompileCalls, CurrencyCalls, GeneratedClasses, GeneratedRoutines, CallDelayMilliseconds, MaximumWrite, NativeWrites, LegacyWrites, FileCopies, WriteDelayMilliseconds, ClosedFiles, CompileItemDelayMilliseconds;
        public static void Reset() { Calls.Clear(); CompiledItems.Clear(); Versions.Clear(); Resource=FailResource=FailNamespace=FailItem=FailCompileResource=CompileErrorMessage=""; Locked=DenyLock=FailLoad=FailSave=Fallback=AlreadyCurrent=FailBatches=FailRewind=AllClassesCurrent=FailGlobals=DenyFileCopy=FailNativeWrite=TruncateNativeWrite=false; Disposed=CompileCalls=CurrencyCalls=GeneratedClasses=GeneratedRoutines=CallDelayMilliseconds=MaximumWrite=NativeWrites=LegacyWrites=FileCopies=WriteDelayMilliseconds=ClosedFiles=CompileItemDelayMilliseconds=0; Cache.Connection=null; }
        public static void Add(string text) { Calls.Add(((Cache.Connection == null) ? "none" : Cache.Connection.Namespace) + ":" + text); }
        public static void ReadResource(string xml) { var match=System.Text.RegularExpressions.Regex.Match(xml,"<!-- Resource:(.*?) -->"); if(match.Success) Resource=match.Groups[1].Value; }
    }
    public class Status { public bool IsOK { get { return Code == 0; } } public int Code; public string Message=""; public Status(int code=0,string message="") { Code=code; Message=message; } public void ThrowException() { throw new Exception(Message); } }
    public class Connection : IDisposable {
        public string Namespace, ServerVersion="2022.1";
        public Connection(string ns) { Namespace=ns; }
        public Command CreateCommand() { return new Command(); }
        public void Dispose() { State.Disposed++; }
    }
    public class Command : IDisposable {
        public string CommandText;
        public int ExecuteNonQuery() { if(CommandText != "DELETE FROM zzzSoftware WHERE Ordner='ServicePack' AND Dateiname='XML'") throw new Exception("unexpected SQL"); State.Add("servicepack-reset"); return 1; }
        public void Dispose() { }
    }
    public static class Cache { public static Connection Connection; }
    public static class Update {
        public static bool TablesCreated, ExitApplication;
        public static void UpdateCreateTable() { if(TablesCreated || ExitApplication) throw new Exception("namespace state was not reset"); TablesCreated=true; State.Add("bootstrap"); }
        public static bool LockExklusiv() { State.Add("lock"); if(State.DenyLock) return false; if(State.Locked) throw new Exception("lock leaked"); State.Locked=true; return true; }
        public static bool UnLockExklusiv() { State.Add("unlock"); State.Locked=false; return true; }
    }
    public class Record { public string Application="", Tabelle="", Version="", dbversion=""; }
    public class VersionObject {
        private Persistent obj;
        public string Application="", Tabelle="", Version="", dbversion="";
        public VersionObject() { obj=new Persistent(this); }
        public void OpenIdBySQL(string sql) {
            string marker="And Tabelle='"; int start=sql.IndexOf(marker)+marker.Length;
            Tabelle=sql.Substring(start,sql.Length-start-1);
            Record record;
            if(State.Versions.TryGetValue(Cache.Connection.Namespace+"/"+Tabelle,out record)) { Version=record.Version; dbversion=record.dbversion; }
            else if(State.AlreadyCurrent) { Version="<Export ts=\"2026-11-05 10:00:00\">"; dbversion="2022.1"; }
        }
        public void Close() { if(obj==null) throw new Exception("missing version object"); }
    }
    public class Persistent {
        readonly VersionObject owner;
        public Persistent(VersionObject value) { owner=value; }
        public Status Save() {
            State.Add("save:"+owner.Tabelle);
            if(State.FailSave) return new Status(1,"save failed");
            State.Versions[Cache.Connection.Namespace+"/"+owner.Tabelle]=new Record { Application=owner.Application,Tabelle=owner.Tabelle,Version=owner.Version,dbversion=owner.dbversion };
            return new Status();
        }
    }
    public class Data {
        readonly string assembly;
        readonly bool includeLegacyData;
        public Data() : this("Nov.NT.Daten") { }
        public Data(string name) : this(name,false) { }
        public Data(string name,bool legacyData) { assembly=name; includeLegacyData=legacyData; }
        public AssemblyName GetName() { return new AssemblyName(assembly); }
        public string[] GetManifestResourceNames() {
            string prefix=assembly=="Aprima.SysDB" ? "AP.SysDB." : assembly+".";
            string[] names=assembly=="Aprima.SysDB" ? (includeLegacyData ? new[] { "Aprima-Datentypen.xml","Aprima-Daten.xml","Aprima-System.xml","Aprima-System-PLZ-D.xml" } : new[] { "Aprima-Datentypen.xml","Aprima-System.xml","Aprima-System-PLZ-D.xml" }) :
                assembly=="Nov.SysDB" ? new[] { "Novacom-System.xml" } : new[] { "Novacom-NT-Daten.xml","Novacom-NT-BOH.xml","Novacom-NT-POS.xml","Novacom-NT-POS-API.xml","Novacom-NT-QRY.xml" };
            for(int i=0;i<names.Length;i++) names[i]=prefix+names[i];
            return names;
        }
        public Stream GetManifestResourceStream(string name) {
            State.Resource=name.Substring((assembly=="Aprima.SysDB" ? "AP.SysDB." : assembly+".").Length);
            return new MemoryStream(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<Export ts=\"2026-11-05 10:00:00\">\n<!-- Resource:"+State.Resource+" -->\n<Routine name=\"ÄTest\"/></Export>"));
        }
    }
    public class Binary {
        readonly MemoryStream bytes=new MemoryStream();
        public NativeStream NativeObject { get; private set; }
        public long Length { get { return bytes.Length; } }
        public Binary(Connection connection) { NativeObject=new NativeStream(this); }
        public void Write(byte[] buffer,int start,int count) { State.LegacyWrites++; if(State.WriteDelayMilliseconds>0) System.Threading.Thread.Sleep(State.WriteDelayMilliseconds); WriteBytes(buffer,start,count); }
        public void WriteBytes(byte[] buffer,int start,int count) { State.MaximumWrite=Math.Max(State.MaximumWrite,count); bytes.Write(buffer,start,count); }
        public Status Clear() { bytes.SetLength(0); bytes.Position=0; return new Status(); }
        public Status Rewind() { bytes.Position=0; return State.FailRewind ? new Status(1,"rewind failed") : new Status(); }
        public string Text { get { return Encoding.UTF8.GetString(bytes.ToArray()); } }
        public byte[] ToArray() { return bytes.ToArray(); }
        public void Close() { bytes.Dispose(); }
    }
    public class ServerFile : IDisposable {
        public string Path;
        public ServerFile NativeObject { get { return this; } }
        public ServerFile(Connection connection,string cls,object[] args) { if(cls!="%Stream.FileBinary" || args.Length!=0) throw new Exception("unexpected stream class"); }
        public ServerFile() { }
        public void InvokeStatusCode(string method,object[] args) {
            if(method!="LinkToFile" || args.Length!=1) throw new Exception("unexpected file operation");
            Path=(string)args[0];
        }
        public void Dispose() { State.ClosedFiles++; }
        public void Close() { Dispose(); }
    }
    public class NativeStream {
        readonly Binary owner;
        public NativeStream(Binary value) { owner=value; }
        public void InvokeStatusCode(string method,object[] args) {
            if(args.Length!=1) throw new Exception("invalid binary argument array");
            if(method=="CopyFrom") {
                // Fail after a partial copy to verify that fallback clears it.
                owner.WriteBytes(new byte[] { 1,2,3 },0,3);
                if(State.DenyFileCopy) throw new Exception("service account cannot read the file");
                owner.Clear(); byte[] data=File.ReadAllBytes(((ServerFile)args[0]).Path);
                owner.WriteBytes(data,0,data.Length); State.FileCopies++; return;
            }
            if(method!="Write" || !(args[0] is byte[])) throw new Exception("native write lost the binary argument");
            if(State.FailNativeWrite) throw new Exception("native write failed");
            byte[] bytes=(byte[])args[0]; State.NativeWrites++;
            if(State.WriteDelayMilliseconds>0) System.Threading.Thread.Sleep(State.WriteDelayMilliseconds);
            owner.WriteBytes(bytes,0,bytes.Length-(State.TruncateNativeWrite ? 1 : 0));
        }
    }
    public class Resolver : IDisposable { public void Dispose() { State.Calls.Add("resolver-disposed"); } }
    public static class GlobalImport {
        public static string LoadStream(Connection c,Binary stream) {
            if(!State.Locked || !stream.Text.Contains("ÄTest")) throw new Exception("global transfer corrupted or unlocked");
            State.ReadResource(stream.Text); State.Add("globals:"+State.Resource);
            return State.FailGlobals ? "global import failed synthetic-secret" : "1";
        }
    }
    public static class Proxy {
        public static Status FileWriteImportXML(Connection c,Binary stream) { if(!State.Locked || !stream.Text.Contains("ÄTest")) throw new Exception("transfer corrupted or unlocked"); State.ReadResource(stream.Text); State.Add("transfer:"+State.Resource); return new Status(); }
        public static Status OBJLoad(Connection c,string path,string flags,ref string errors,ref string items) {
            if(path!="Import.xml" || flags!="fv/display=none") throw new Exception("wrong load arguments");
            State.Add("load:"+State.Resource);
            items="Test.Routine.mac,Test.Current.cls,Test.Class.cls,Test.After.cls,Test.AfterRoutine.mac,Test.Include.inc";
            if(State.GeneratedClasses+State.GeneratedRoutines>0) {
                var names=new List<string>();
                for(int index=0; index<State.GeneratedClasses; index++) names.Add("Fixture.Class"+index.ToString("D5")+".cls");
                for(int index=0; index<State.GeneratedRoutines; index++) names.Add("Fixture.Routine"+index.ToString("D5")+".mac");
                items=String.Join(",",names);
            }
            if(State.FailLoad || State.FailResource==State.Resource || State.FailNamespace==c.Namespace) return new Status(1,"load failed synthetic-secret");
            return new Status();
        }
        public static string OBJIsUpToDate(Connection c,string name,bool? flag,long? value) {
            State.CurrencyCalls++;
            if(State.CallDelayMilliseconds>0) System.Threading.Thread.Sleep(State.CallDelayMilliseconds);
            return name=="Test.Current" || State.AllClassesCurrent ? "1" : "0";
        }
        public static Status OBJCompileList(Connection c,string item,string flags,string errors) {
            State.CompileCalls++;
            if(State.CallDelayMilliseconds>0) System.Threading.Thread.Sleep(State.CallDelayMilliseconds);
            State.Add("compile:"+State.Resource+":"+item+":"+flags);
            if(Encoding.UTF8.GetByteCount(item)>24000) throw new Exception("Compiler batch exceeds the provider string bound");
            if(State.Fallback && flags.StartsWith("frvkb/",StringComparison.Ordinal)) return new Status(5380,"deployed");
            if(State.FailBatches && item.Contains(",")) return new Status(1,"batch compile failed");
            foreach(string name in item.Split(',')) {
                if(name==State.FailItem && (State.FailCompileResource=="" || State.FailCompileResource==State.Resource)) return new Status(1,State.CompileErrorMessage.Length == 0 ? "compile failed" : State.CompileErrorMessage);
            }
            foreach(string name in item.Split(',')) {
                if((name=="Test.Current.cls" || (State.AllClassesCurrent && name.EndsWith(".cls"))) && flags.Contains("/checkuptodate=all")) continue;
                if(State.CompileItemDelayMilliseconds>0) System.Threading.Thread.Sleep(State.CompileItemDelayMilliseconds);
                State.CompiledItems.Add(c.Namespace+":"+State.Resource+":"+name);
            }
            return new Status();
        }
        public static string SQLPurge(Connection c) { State.Add("purge"); return "1"; }
        public static string FileDelete(Connection c,string path) { if(!State.Locked) throw new Exception("cleanup without ownership"); State.Add("delete:"+path); return "1"; }
    }
    public class ShortReadStream : MemoryStream {
        public ShortReadStream(byte[] bytes) : base(bytes,false) { }
        public override int Read(byte[] buffer,int offset,int count) { return base.Read(buffer,offset,Math.Min(count,997)); }
    }
}
