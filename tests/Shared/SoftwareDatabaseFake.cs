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
    public sealed class Status { public bool IsOK { get { return true; } } }
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
        public static List<Entry> Entries = new List<Entry>();
        public static List<string> Events = new List<string>();
        public static string FailTarget = "";
        public static string FailOpenTarget = "";
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
        public string ConnectionString { get; set; }
        public string Target, Namespace;
        public void Open()
        {
            var b = new DbConnectionStringBuilder(); b.ConnectionString = ConnectionString;
            Namespace = (string)b["Namespace"]; Target = b["Server"] + ((string)b["Server"] == "127.0.0.1" ? ":" + b["Port"] : "") + "/" + Namespace;
            Database.Events.Add("open:" + Target);
            if (Database.FailOpenTarget == Target) throw new Exception("Password=synthetic-secret; simulated login failure");
        }
        public Signature GetMtdSignature() { return new Signature(); }
        public Transaction BeginTransaction() { return new Transaction(this); }
        public Command CreateCommand() { return new Command(this); }
        public void Dispose() { Database.Events.Add("close:" + Target); }
    }
    public sealed class Transaction : IDisposable
    {
        private readonly Connection connection;
        private readonly List<Entry> snapshot;
        private bool finished;
        public Transaction(Connection value)
        {
            connection = value; snapshot = Database.Entries.ToList();
            Database.Events.Add("begin:" + connection.Target);
        }
        public void Commit() { finished = true; Database.Events.Add("commit:" + connection.Target); }
        public void Rollback()
        {
            if (finished) throw new Exception("Transaction already completed");
            Database.Entries = snapshot; finished = true; Database.Events.Add("rollback:" + connection.Target);
        }
        public void Dispose() { if (!finished) Rollback(); }
    }
    public sealed class Parameter { public object Value { get; set; } }
    public sealed class Command : IDisposable
    {
        private readonly Connection connection;
        public string CommandText { get; set; }
        public int CommandTimeout { get; set; }
        public Transaction Transaction { get; set; }
        public List<Parameter> Parameters = new List<Parameter>();
        public Command(Connection value) { connection = value; }
        public Parameter CreateParameter() { return new Parameter(); }
        public int ExecuteNonQuery()
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
        public void Clear() { SetLength(0); }
        public Status Save() { return new Status(); }
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
            if (className != "%Dictionary.CompiledClass" || method != "%ExistsId" || signature.Arguments[0] != "User.zzzSoftware") throw new Exception("Unexpected class method");
            signature.ReturnValue.Value = connection.Namespace != "%SYS" && !Database.MissingSchemaNamespaces.Contains(connection.Namespace);
        }
        public void SetProperty(string name, Signature signature) { properties[name] = signature.Arguments[0]; }
        public void GetProperty(string name, Signature signature)
        {
            if (name != "Datei") throw new Exception("Unexpected property");
            signature.ReturnValue.Value = stream;
        }
        public void RunMethod(string method, Signature signature)
        {
            switch (method)
            {
                case "Execute": signature.ReturnValue.Value = new Status(); break;
                case "Next": signature.ReturnValue.Value = ++position < Database.Namespaces.Length ? 1 : 0; break;
                case "Get": signature.ReturnValue.Value = Database.Namespaces[position]; break;
                case "%Save":
                    Database.Entries.Add(new Entry { Target = connection.Target, Application = properties["Application"], Version = properties["Version"],
                        Ordner = properties["Ordner"], Dateiname = properties["Dateiname"], Pfad = properties["Pfad"], Bytes = stream.ToArray() });
                    signature.ReturnValue.Value = new Status(); break;
                default: throw new Exception("Unexpected method");
            }
        }
        public void Close() { }
    }
}
