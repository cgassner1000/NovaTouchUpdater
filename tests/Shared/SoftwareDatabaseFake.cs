// In-memory implementation of the provider surface used by SoftwareDatabase.ps1.
// No sockets, registry access, application assemblies or real databases.
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;

namespace SoftwareDatabaseFixture
{
    public enum ClientType { tStatus, tString, tInt, tBool, tObject }
    public sealed class Status
    {
        public string Error = "";
        public bool IsOK { get { return Error.Length == 0; } }
        public void ThrowException() { if (!IsOK) throw new Exception(Error); }
    }
    public sealed class ReturnValue { public object Value { get; set; } }
    public sealed class Signature
    {
        public ReturnValue ReturnValue = new ReturnValue();
        public List<string> Arguments = new List<string>();
        public void SetReturnType(Connection connection, ClientType type) { }
        public void SetReturnType(Connection connection, Type type)
        {
            if (type != typeof(BinaryStream)) throw new Exception("Unexpected stream projection");
        }
        public void Add(string value, bool byReference) { Arguments.Add(value); }
        public void Clear() { Arguments.Clear(); ReturnValue.Value = null; }
    }
    public sealed class Entry
    {
        public string Target, Application, Version, Ordner, Pfad, Dateiname;
        public byte[] Bytes;
    }
    public static class Database
    {
        public static readonly object Gate = new object();
        public static List<Entry> Entries = new List<Entry>();
        public static List<string> Events = new List<string>();
        public static int WriteDelayMilliseconds, ActiveTransactions, PeakTransactions;
        public static void Record(string value) { lock (Gate) Events.Add(value); }
        public static string FailTarget = "";
        public static string FailOpenTarget = "";
        public static string FailStreamOperation = "";
        public static bool FailObjectSave;
        public static string[] Namespaces = { "%SYS", "MANDANT1", "MANDANT2" };
        public static string[] MissingSchemaNamespaces = { "EMPTY" };
    }
    public sealed class Builder
    {
        public string Server { get; set; }
        public int Port { get; set; }
        public string Namespace { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public bool Ssl { get; set; }
        public bool Pooling { get; set; }
        public int ConnectionTimeout { get; set; }
        public string ConnectionString
        {
            get { var b = new DbConnectionStringBuilder(); b["Server"] = Server; b["Port"] = Port; b["Namespace"] = Namespace; return b.ConnectionString; }
        }
    }
    public sealed class Connection : IDisposable
    {
        private readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private void AssertThread() { if (ownerThread != System.Threading.Thread.CurrentThread.ManagedThreadId) throw new Exception("Connection crossed runspace threads"); }
        public System.Reflection.Assembly GeneratedAssembly;
        public string ConnectionString { get; set; }
        public string Target, Namespace;
        public void Open()
        {
            AssertThread();
            var b = new DbConnectionStringBuilder(); b.ConnectionString = ConnectionString;
            Namespace = (string)b["Namespace"]; Target = b["Server"] + ((string)b["Server"] == "127.0.0.1" ? ":" + b["Port"] : "") + "/" + Namespace;
            Database.Record("open:" + Target);
            if (Database.FailOpenTarget == Target) throw new Exception("Password=synthetic-secret; simulated login failure");
        }
        public Signature GetMtdSignature() { AssertThread(); return new Signature(); }
        public Transaction BeginTransaction() { AssertThread(); return new Transaction(this); }
        public Command CreateCommand() { AssertThread(); return new Command(this); }
        public void Dispose() { AssertThread(); Database.Record("close:" + Target); }
    }
    public sealed class Transaction : IDisposable
    {
        private readonly Connection connection;
        private readonly List<Entry> snapshot;
        private bool finished;
        public Transaction(Connection value)
        {
            connection = value;
            lock (Database.Gate)
            {
                snapshot = Database.Entries.Where(e => e.Target == connection.Target).ToList();
                Database.PeakTransactions = Math.Max(Database.PeakTransactions, ++Database.ActiveTransactions);
                Database.Events.Add("begin:" + connection.Target);
            }
        }
        public void Commit() { lock (Database.Gate) { finished = true; Database.ActiveTransactions--; Database.Events.Add("commit:" + connection.Target); } }
        public void Rollback()
        {
            if (finished) throw new Exception("Transaction already completed");
            lock (Database.Gate)
            {
                Database.Entries.RemoveAll(e => e.Target == connection.Target);
                Database.Entries.AddRange(snapshot); finished = true; Database.ActiveTransactions--;
                Database.Events.Add("rollback:" + connection.Target);
            }
        }
        public void Dispose() { if (!finished) Rollback(); }
    }
    public sealed class Parameter { public object Value { get; set; } }
    public sealed class Reader : IDisposable
    {
        private readonly string[] values; private int index=-1;
        public Reader(string[] items) { values=items; }
        public bool Read() { return ++index < values.Length; }
        public object GetValue(int ordinal) { return values[index]; }
        public void Dispose() { }
    }
    public sealed class Command : IDisposable
    {
        private readonly Connection connection;
        public string CommandText { get; set; }
        public int CommandTimeout { get; set; }
        public Transaction Transaction { get; set; }
        public List<Parameter> Parameters = new List<Parameter>();
        public Command(Connection value) { connection = value; }
        public Parameter CreateParameter() { return new Parameter(); }
        public Reader ExecuteReader()
        {
            lock (Database.Gate)
                return new Reader(Database.Entries.Where(e => e.Target == connection.Target && e.Application == (string)Parameters[0].Value && e.Ordner == "Setup" && e.Pfad == "0" && e.Dateiname == "MSI").Select(e => e.Version).ToArray());
        }
        public int ExecuteNonQuery()
        {
            lock (Database.Gate) return ExecuteLocked();
        }
        private int ExecuteLocked()
        {
            if (Transaction == null) throw new Exception("Missing SQL transaction");
            string application = (string)Parameters[0].Value;
            if (CommandText.StartsWith("DELETE"))
                return Database.Entries.RemoveAll(e => e.Target == connection.Target && e.Application == application && (e.Ordner == "Setup" || e.Ordner == "ServicePacks"));
            if (CommandText.StartsWith("UPDATE"))
            {
                if (Database.FailTarget == connection.Target) throw new Exception("Password=synthetic-secret; simulated provider failure");
                string temporary = (string)Parameters[1].Value;
                foreach (var entry in Database.Entries.Where(e => e.Target == connection.Target && e.Application == temporary)) entry.Application = application;
                return 1;
            }
            throw new Exception("Unexpected SQL");
        }
        public void Dispose() { }
    }
    public sealed class BinaryStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Database.WriteDelayMilliseconds > 0) System.Threading.Thread.Sleep(Database.WriteDelayMilliseconds);
            base.Write(buffer, offset, count);
        }
        public Status Clear()
        {
            if (Database.FailStreamOperation == "Clear") return new Status { Error = "IRIS status 5001: simulated stream clear failure; Password=synthetic-secret" };
            SetLength(0); return new Status();
        }
        public Status Save()
        {
            return new Status { Error = Database.FailStreamOperation == "Save" ? "IRIS status 5002: simulated stream save failure; Password=synthetic-secret" : "" };
        }
    }
    public sealed class DynamicObject
    {
        private readonly Connection connection;
        private readonly string className;
        private int position = -1;
        private readonly Dictionary<string, string> properties = new Dictionary<string, string>();
        private readonly BinaryStream stream = new BinaryStream();
        public DynamicObject(Connection value, string name, object[] arguments)
        {
            connection = value; className = name;
            if (className == "%ResultSet" && (string)arguments[0] != "%SYS.Namespace:List") throw new Exception("Unexpected class query");
        }
        public static void RunClassMethod(Connection connection, string className, string method, Signature signature)
        {
            throw new NullReferenceException("Dynamic class metadata must not be queried by the uploader");
        }
        public static void RunClassMethodS(Connection connection, string className, string method, Signature signature)
        {
            if (className != "%Dictionary.CompiledClass" || method != "%ExistsId" || signature.Arguments[0] != "User.zzzSoftware") throw new Exception("Unexpected class method");
            signature.ReturnValue.Value = connection.Namespace != "%SYS" && !Database.MissingSchemaNamespaces.Contains(connection.Namespace);
        }
        public void SetProperty(string name, Signature signature) { throw new NullReferenceException("Dynamic property metadata lookup failed"); }
        public void SetPropertyS(string name, Signature signature) { properties[name] = signature.Arguments[0]; }
        public void GetProperty(string name, Signature signature)
        {
            throw new NullReferenceException("Dynamic property metadata lookup failed");
        }
        public void GetPropertyS(string name, Signature signature)
        {
            if (name != "Datei") throw new Exception("Unexpected property");
            if (connection.GeneratedAssembly == null) throw new Exception("The binding assembly is required for binary stream projection");
            signature.ReturnValue.Value = stream;
        }
        public void RunMethod(string method, Signature signature)
        {
            throw new NullReferenceException("Dynamic method metadata lookup failed");
        }
        public void RunMethodS(string method, Signature signature)
        {
            switch (method)
            {
                case "Execute": signature.ReturnValue.Value = new Status(); break;
                case "Next": signature.ReturnValue.Value = ++position < Database.Namespaces.Length ? 1 : 0; break;
                case "Get": signature.ReturnValue.Value = Database.Namespaces[position]; break;
                case "%Save":
                    if (Database.FailObjectSave)
                    {
                        signature.ReturnValue.Value = new Status { Error = "IRIS status 5003: simulated object save failure; Password=synthetic-secret" };
                        break;
                    }
                    lock (Database.Gate) Database.Entries.Add(new Entry { Target = connection.Target, Application = properties["Application"], Version = properties["Version"],
                        Ordner = properties["Ordner"], Dateiname = properties["Dateiname"], Pfad = properties["Pfad"], Bytes = stream.ToArray() });
                    signature.ReturnValue.Value = new Status(); break;
                default: throw new Exception("Unexpected method");
            }
        }
        public void Close() { }
    }
}
