using System;
using System.Collections.Generic;
using System.Text;

namespace ServicePackFixture
{
    public enum ClientType { tStatus }
    public sealed class Status {
        public string Error = "";
        public bool IsOK { get { return Error.Length == 0; } }
        public void ThrowException() { throw new Exception(Error); }
    }
    public sealed class ResultValue { public object ValueObject; public object Value { get { return ValueObject; } set { ValueObject = value; } } }
    public sealed class Signature {
        public ResultValue ReturnValue = new ResultValue();
        public List<object> Arguments = new List<object>();
        public void SetReturnType(Connection connection, ClientType type) { }
        public void Add(string value, bool byReference) { Arguments.Add(value); }
        public void Add(ProxyObject value, Connection connection, Type type, bool byReference) {
            if (type != typeof(ProxyObject)) throw new Exception("Wrong object binding");
            Arguments.Add(value);
        }
        public void Clear() { Arguments.Clear(); ReturnValue.Value = null; }
    }
    public static class Database {
        public static List<string> Events = new List<string>();
        public static string FailCompile = "", FailLoad = "";
        public static int Disposed, Cleared, Closed;
        public static void Reset() { Events.Clear(); FailCompile = FailLoad = ""; Disposed = Cleared = Closed = 0; }
    }
    public sealed class Connection : IDisposable {
        public string Namespace;
        public Connection(string ns) { Namespace = ns; }
        public Signature GetMtdSignature() { return new Signature(); }
        public void Dispose() { Database.Disposed++; }
    }
    public sealed class ProxyObject {
        private readonly Connection connection;
        private readonly StringBuilder contents = new StringBuilder();
        public ProxyObject(Connection connection, string name, object[] arguments) {
            if (name != "%Stream.GlobalCharacter") throw new Exception("Unexpected class " + name);
            this.connection = connection;
        }
        public void RunMethodS(string method, Signature signature) {
            if (method == "Write") contents.Append((string)signature.Arguments[0]);
            else if (method == "Clear") { contents.Clear(); Database.Cleared++; }
            else if (method != "Rewind") throw new Exception("Unexpected stream method " + method);
            signature.ReturnValue.Value = new Status();
        }
        public static void RunClassMethodS(Connection connection, string name, string method, Signature signature) {
            if (name != "%SYSTEM.OBJ") throw new Exception("Unexpected class " + name);
            var status = new Status();
            if (method == "LoadStream") {
                var stream = signature.Arguments[0] as ProxyObject;
                if (stream == null || !object.ReferenceEquals(stream.connection, connection)) throw new Exception("Stream must use native object binding");
                if ((string)signature.Arguments[1] != "/compile=0") throw new Exception("Import must precede compilation");
                Database.Events.Add(connection.Namespace + "|load|" + stream.contents.ToString());
                if (connection.Namespace == Database.FailLoad) status.Error = "simulated XML import error; Password=synthetic-secret";
            } else if (method == "CompileList") {
                if ((string)signature.Arguments[1] != "ckbf") throw new Exception("Compiler flags changed");
                Database.Events.Add(connection.Namespace + "|compile|" + signature.Arguments[0] + "|ckbf");
                if (connection.Namespace == Database.FailCompile) status.Error = "simulated compiler error";
            } else throw new Exception("Unexpected method " + method);
            signature.ReturnValue.Value = status;
        }
        public void Close() { Database.Closed++; }
    }
}
