using System;
using System.Collections.Generic;
using System.IO;

namespace ServicePackFixture
{
    public static class Database {
        public static List<string> Events = new List<string>();
        public static List<string> LoadPaths = new List<string>();
        public static string FailCompile = "", FailLoad = "", ThrowLoad = "", DisconnectLoad = "";
        public static int Disposed;
        public static void Reset() {
            Events.Clear(); LoadPaths.Clear();
            FailCompile = FailLoad = ThrowLoad = DisconnectLoad = "";
            Disposed = 0;
        }
    }
    public sealed class Connection : IDisposable {
        public string Namespace;
        public NativeMethods NativeAPI { get; private set; }
        public Connection(string ns) { Namespace = ns; NativeAPI = new NativeMethods(this); }
        public object GetMtdSignature() { throw new Exception("Servicepacks must use the native status API, not object bindings"); }
        public void Dispose() { Database.Disposed++; }
    }
    public sealed class NativeMethods {
        private readonly Connection connection;
        public NativeMethods(Connection connection) { this.connection = connection; }
        public void ClassMethodStatusCode(string name, string method, params object[] arguments) {
            foreach (var argument in arguments) {
                if (!(argument is string)) throw new Exception("Only scalar arguments may cross this class-method boundary");
            }
            if (name != "%SYSTEM.OBJ") throw new Exception("Unexpected class " + name);
            if (arguments.Length != 2) throw new Exception("The method needs a path/list and compiler qualifiers");
            if (method == "Load") {
                string path = (string)arguments[0];
                if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new Exception("Load needs the existing XML on this host");
                if ((string)arguments[1] != "fck/display=none") throw new Exception("Load must use fck and suppress terminal output with display=none, never display=0");
                Database.LoadPaths.Add(path);
                Database.Events.Add(connection.Namespace + "|load|" + File.ReadAllText(path));
                if (connection.Namespace == Database.ThrowLoad) throw new Exception("Exception thrown on server (code = 1217) : <THROW>");
                if (connection.Namespace == Database.DisconnectLoad) throw new Exception("Communication error: Server closed communication device <TCP>");
                if (connection.Namespace == Database.FailLoad) throw new Exception("ERROR #5002: simulated XML import error; Password=synthetic-secret");
                if (connection.Namespace == Database.FailCompile) throw new Exception("ERROR #5002: simulated compiler error");
                Database.Events.Add(connection.Namespace + "|compile|" + Path.GetFileName(path) + "|fck");
            } else throw new Exception("Unexpected method " + method);
        }
    }
}
