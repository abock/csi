//
// repl.cs: Support for using the compiler in interactive mode (read-eval-print loop)
//
// Authors:
//   Miguel de Icaza (miguel@gnome.org)
//   Aaron Bockover (abockover@novell.com)
//
// Dual licensed under the terms of the MIT X11 or GNU GPL
//
// Copyright 2001, 2002, 2003 Ximian, Inc (http://www.ximian.com)
// Copyright 2004, 2005, 2006, 2007, 2008, 2009 Novell, Inc
//
//
// TODO:
//   Do not print results in Evaluate, do that elsewhere in preparation for Eval refactoring.
//   Driver.PartialReset should not reset the coretypes, nor the optional types, to avoid
//      computing that on every call.
//
using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

using Mono.CSharp;
using Mono.Attach;
using Mono.Options;

namespace Mono {

	internal class CsharpOptionSet : OptionSet {

		public string ScriptFile { get; private set; }
		public List<string> ScriptArguments { get; private set; }

		protected override bool Parse (string option, OptionContext context)
		{
			if (ScriptArguments != null) {
				if (ScriptFile == null) {
					ScriptFile = option;
				} else {
					ScriptArguments.Add (option);
				}
				return true;
			} else if (option == "-s" || option == "/s" ||
				option == "--script" || option == "/script") {
				ScriptArguments = new List<string> ();
			}

			return base.Parse (option, context);
		}
	}

	public class Driver {
		
		static int Main (string [] args)
		{
			bool show_help = false;
			List<string> evaluator_args = new List<string> ();

			var options = new CsharpOptionSet () {
				{ 
					"a|attach=", 
				  	"Attach to and execute code against an existing Mono process",
					v => {
						int attach_pid = Int32.Parse (v);
						new ClientCSharpShell (attach_pid).Run (null);
						Environment.Exit (0);
					}
				},

				{ 
					"agent=", 
					"Run the C# agent", 
					v => {
						new CSharpAgent ("--agent:" + v);
						Environment.Exit (0);
					}
				},

				{
					"s|script",
					"Run csi as a script supporting arguments. Any arguments passed " +
						"after the script path will be passed to the script and not " +
						"to the C# shell itself.",
					v => { }
				},

				{ "h|help", "Show this help", v => show_help = v != null },

				{ "<>", "Pass-thru to the evaluator", v => evaluator_args.Add (v) }
			};

			List<string> paths;

			try {
				paths = options.Parse (args);
			} catch (OptionException e) {
				Console.Write ("csi: ");
				Console.WriteLine (e.Message);
				Console.WriteLine ("Try --help for more information");
				return 1;
			}

			if (show_help) {
				Console.WriteLine ("Usage: csi [OPTIONS]+ [<source>] [-s [script args]]");
				Console.WriteLine ();
				options.WriteOptionDescriptions (Console.Out);
				return 1;
			}

			string [] startup_files = null;

			try {
				if (options.ScriptFile != null) {
					startup_files = new string [] { options.ScriptFile };
					Evaluator.Init (evaluator_args.ToArray ());
				} else {
					foreach (var path in paths) {
						evaluator_args.Add (path);
					}
					startup_files = Evaluator.InitAndGetStartupFiles (evaluator_args.ToArray ());
				}

				Evaluator.InteractiveBaseClass = typeof (InteractiveBaseShell);
			} catch {
				return 1;
			}


			if (options.ScriptFile != null) {
				InteractiveBaseShell.CommandLineArgs = options.ScriptArguments;
			}

			return new CSharpShell ().Run (startup_files);
		}
	}

	public class InteractiveBaseShell : InteractiveBase {
		static bool tab_at_start_completes;
		
		static InteractiveBaseShell ()
		{
			tab_at_start_completes = false;
		}

		internal static Mono.Terminal.LineEditor Editor;
		
		public static bool TabAtStartCompletes {
			get {
				return tab_at_start_completes;
			}

			set {
				tab_at_start_completes = value;
				if (Editor != null)
					Editor.TabAtStartCompletes = value;
			}
		}

        public static List<string> CommandLineArgs { get; internal set; }

		public static new string help {
			get {
				return InteractiveBase.help +
					"  TabAtStartCompletes - Whether tab will complete even on emtpy lines\n";
			}
		}
	}
	
	public class CSharpShell {
		static bool isatty = true;
		string [] startup_files;
		
		Mono.Terminal.LineEditor editor;
		bool dumb;

		protected virtual void ConsoleInterrupt (object sender, ConsoleCancelEventArgs a)
		{
			// Do not about our program
			a.Cancel = true;

			Mono.CSharp.Evaluator.Interrupt ();
		}
		
		void SetupConsole ()
		{
			string term = Environment.GetEnvironmentVariable ("TERM");
			dumb = term == "dumb" || term == null || isatty == false;
			
			editor = new Mono.Terminal.LineEditor ("csharp", 300);
			InteractiveBaseShell.Editor = editor;

			editor.AutoCompleteEvent += delegate (string s, int pos){
				string prefix = null;

				string complete = s.Substring (0, pos);
				
				string [] completions = Evaluator.GetCompletions (complete, out prefix);
				
				return new Mono.Terminal.LineEditor.Completion (prefix, completions);
			};
			
#if false
			//
			// This is a sample of how completions sould be implemented.
			//
			editor.AutoCompleteEvent += delegate (string s, int pos){

				// Single match: "Substring": Sub-string
				if (s.EndsWith ("Sub")){
					return new string [] { "string" };
				}

				// Multiple matches: "ToString" and "ToLower"
				if (s.EndsWith ("T")){
					return new string [] { "ToString", "ToLower" };
				}
				return null;
			};
#endif
			
			Console.CancelKeyPress += ConsoleInterrupt;
		}

		string GetLine (bool primary)
		{
			string prompt = primary ? InteractiveBase.Prompt : InteractiveBase.ContinuationPrompt;

			if (dumb){
				if (isatty)
					Console.Write (prompt);

				return Console.ReadLine ();
			} else {
				return editor.Edit (prompt, "");
			}
		}

		delegate string ReadLiner (bool primary);

		void InitializeUsing ()
		{
			Evaluate ("using System; using System.Linq; using System.Collections.Generic; using System.Collections;");
		}

		void InitTerminal ()
		{
			isatty = UnixUtils.isatty (0) && UnixUtils.isatty (1);

			// Work around, since Console is not accounting for
			// cursor position when writing to Stderr.  It also
			// has the undesirable side effect of making
			// errors plain, with no coloring.
			Report.Stderr = Console.Out;
			SetupConsole ();

			if (isatty)
				Console.WriteLine ("Mono C# Shell, type \"help;\" for help\n\nEnter statements below.");

		}

		void ExecuteSources (IEnumerable<string> sources, bool ignore_errors)
		{
			foreach (string file in sources){
				try {
					try {
						bool first_line = true;
						using (System.IO.StreamReader r = System.IO.File.OpenText (file)){
							ReadEvalPrintLoopWith (p => {
								var line = r.ReadLine ();
								// Ignore the shebang if we have one
								if (first_line && line.StartsWith ("#!")) {
									return String.Empty;
								}
								first_line = false;
								return r.ReadLine ();
							});
						}
					} catch (FileNotFoundException){
						Console.Error.WriteLine ("cs2001: Source file `{0}' not found", file);
						return;
					}
				} catch {
					if (!ignore_errors)
						throw;
				}
			}
		}
		
		protected virtual void LoadStartupFiles ()
		{
			string dir = Path.Combine (
				Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData),
				"csharp");
			if (!Directory.Exists (dir))
				return;

			List<string> sources = new List<string> ();
			List<string> libraries = new List<string> ();
			
			foreach (string file in System.IO.Directory.GetFiles (dir)){
				string l = file.ToLower ();
				
				if (l.EndsWith (".cs"))
					sources.Add (file);
				else if (l.EndsWith (".dll"))
					libraries.Add (file);
			}

			foreach (string file in libraries)
				Evaluator.LoadAssembly (file);

			ExecuteSources (sources, true);
		}

		void ReadEvalPrintLoopWith (ReadLiner readline)
		{
			string expr = null;
			while (!InteractiveBase.QuitRequested){
				string input = readline (expr == null);
				if (input == null)
					return;

				if (input == "")
					continue;

				expr = expr == null ? input : expr + "\n" + input;
				
				expr = Evaluate (expr);
			} 
		}

		public int ReadEvalPrintLoop ()
		{
			if (startup_files.Length == 0)
				InitTerminal ();

			InitializeUsing ();

			LoadStartupFiles ();

			//
			// Interactive or startup files provided?
			//
			if (startup_files.Length != 0)
				ExecuteSources (startup_files, false);
			else
				ReadEvalPrintLoopWith (GetLine);

			return 0;
		}

		internal protected virtual string Evaluate (string input)
		{
			bool result_set;
			object result;

			try {
				input = Evaluator.Evaluate (input, out result, out result_set);

				if (result_set){
					PrettyPrint (Console.Out, result);
					Console.WriteLine ();
				}
			} catch (Exception e){
				Console.WriteLine (e);
				return null;
			}
			
			return input;
		}

		static void p (TextWriter output, string s)
		{
			output.Write (s);
		}

		internal static string EscapeString (string s)
		{
			return s.Replace ("\"", "\\\"");
		}
		
		static void EscapeChar (TextWriter output, char c)
		{
			if (c == '\''){
				output.Write ("'\\''");
				return;
			}
			if (c > 32){
				output.Write ("'{0}'", c);
				return;
			}
			switch (c){
			case '\a':
				output.Write ("'\\a'");
				break;

			case '\b':
				output.Write ("'\\b'");
				break;
				
			case '\n':
				output.Write ("'\\n'");
				break;
				
			case '\v':
				output.Write ("'\\v'");
				break;
				
			case '\r':
				output.Write ("'\\r'");
				break;
				
			case '\f':
				output.Write ("'\\f'");
				break;
				
			case '\t':
				output.Write ("'\\t");
				break;

			default:
				output.Write ("'\\x{0:x}", (int) c);
				break;
			}
		}
		
		internal static void PrettyPrint (TextWriter output, object result)
		{
			if (result == null){
				p (output, "null");
				return;
			}
			
			if (result is Array){
				Array a = (Array) result;
				
				p (output, "{ ");
				int top = a.GetUpperBound (0);
				for (int i = a.GetLowerBound (0); i <= top; i++){
					PrettyPrint (output, a.GetValue (i));
					if (i != top)
						p (output, ", ");
				}
				p (output, " }");
			} else if (result is bool){
				if ((bool) result)
					p (output, "true");
				else
					p (output, "false");
			} else if (result is string){
				p (output, String.Format ("\"{0}\"", EscapeString ((string)result)));
			} else if (result is IDictionary){
				IDictionary dict = (IDictionary) result;
				int top = dict.Count, count = 0;
				
				p (output, "{");
				foreach (DictionaryEntry entry in dict){
					count++;
					p (output, "{ ");
					PrettyPrint (output, entry.Key);
					p (output, ", ");
					PrettyPrint (output, entry.Value);
					if (count != top)
						p (output, " }, ");
					else
						p (output, " }");
				}
				p (output, "}");
			} else if (result is IEnumerable) {
				int i = 0;
				p (output, "{ ");
				foreach (object item in (IEnumerable) result) {
					if (i++ != 0)
						p (output, ", ");

					PrettyPrint (output, item);
				}
				p (output, " }");
			} else if (result is char) {
				EscapeChar (output, (char) result);
			} else {
				p (output, result.ToString ());
			}
		}

		public CSharpShell ()
		{
		}

		public virtual int Run (string [] startup_files)
		{
			this.startup_files = startup_files;
			return ReadEvalPrintLoop ();
		}
		
	}

	//
	// A shell connected to a CSharpAgent running in a remote process.
	//  - maybe add 'class_name' and 'method_name' arguments to LoadAgent.
	//  - Support Gtk and Winforms main loops if detected, this should
	//    probably be done as a separate agent in a separate place.
	//
	class ClientCSharpShell : CSharpShell {
		NetworkStream ns, interrupt_stream;
		
		public ClientCSharpShell (int pid)
		{
			// Create a server socket we listen on whose address is passed to the agent
			TcpListener listener = new TcpListener (new IPEndPoint (IPAddress.Loopback, 0));
			listener.Start ();
			TcpListener interrupt_listener = new TcpListener (new IPEndPoint (IPAddress.Loopback, 0));
			interrupt_listener.Start ();
	
			string agent_assembly = typeof (ClientCSharpShell).Assembly.Location;
			string agent_arg = String.Format ("--agent:{0}:{1}" ,
							  ((IPEndPoint)listener.Server.LocalEndPoint).Port,
							  ((IPEndPoint)interrupt_listener.Server.LocalEndPoint).Port);
	
			VirtualMachine vm = new VirtualMachine (pid);
			vm.Attach (agent_assembly, agent_arg);
	
			/* Wait for the client to connect */
			TcpClient client = listener.AcceptTcpClient ();
			ns = client.GetStream ();
			TcpClient interrupt_client = interrupt_listener.AcceptTcpClient ();
			interrupt_stream = interrupt_client.GetStream ();
	
			Console.WriteLine ("Connected.");
		}
	
		//
		// A remote version of Evaluate
		//
		internal protected override string Evaluate (string input)
		{
			ns.WriteString (input);
			while (true) {
				AgentStatus s = (AgentStatus) ns.ReadByte ();
	
				switch (s){
				case AgentStatus.PARTIAL_INPUT:
					return input;
	
				case AgentStatus.ERROR:
					string err = ns.GetString ();
					Console.Error.WriteLine (err);
					break;
	
				case AgentStatus.RESULT_NOT_SET:
					return null;
	
				case AgentStatus.RESULT_SET:
					string res = ns.GetString ();
					Console.WriteLine (res);
					return null;
				}
			}
		}
		
		public override int Run (string [] startup_files)
		{
			// The difference is that we do not call Evaluator.Init, that is done on the target
			return ReadEvalPrintLoop ();
		}
	
		protected override void ConsoleInterrupt (object sender, ConsoleCancelEventArgs a)
		{
			// Do not about our program
			a.Cancel = true;
	
			interrupt_stream.WriteByte (0);
			int c = interrupt_stream.ReadByte ();
			if (c != -1)
				Console.WriteLine ("Execution interrupted");
		}
			
	}

	//
	// Stream helper extension methods
	//
	public static class StreamHelper {
		static DataConverter converter = DataConverter.LittleEndian;
		
		public static int GetInt (this Stream stream)
		{
			byte [] b = new byte [4];
			if (stream.Read (b, 0, 4) != 4)
				throw new IOException ("End reached");
			return converter.GetInt32 (b, 0);
		}
		
		public static string GetString (this Stream stream)
		{
			int len = stream.GetInt ();
			byte [] b = new byte [len];
			if (stream.Read (b, 0, len) != len)
				throw new IOException ("End reached");
			return Encoding.UTF8.GetString (b);
		}
	
		public static void WriteInt (this Stream stream, int n)
		{
			byte [] bytes = converter.GetBytes (n);
			stream.Write (bytes, 0, bytes.Length);
		}
	
		public static void WriteString (this Stream stream, string s)
		{
			stream.WriteInt (s.Length);
			byte [] bytes = Encoding.UTF8.GetBytes (s);
			stream.Write (bytes, 0, bytes.Length);
		}
	}
	
	public enum AgentStatus : byte {
		// Received partial input, complete
		PARTIAL_INPUT  = 1,
	
		// The result was set, expect the string with the result
		RESULT_SET     = 2,
	
		// No result was set, complete
		RESULT_NOT_SET = 3,
	
		// Errors and warnings string follows
		ERROR          = 4, 
	}
	
	//
	// This is the agent loaded into the target process when using --attach.
	//
	class CSharpAgent
	{
		NetworkStream interrupt_stream;
		
		public CSharpAgent (String arg)
		{
			new Thread (new ParameterizedThreadStart (Run)).Start (arg);
		}

		public void InterruptListener ()
		{
			while (true){
				int b = interrupt_stream.ReadByte();
				if (b == -1)
					return;
				Evaluator.Interrupt ();
				interrupt_stream.WriteByte (0);
			}
		}
		
		public void Run (object o)
		{
			string arg = (string)o;
			string ports = arg.Substring (8);
			int sp = ports.IndexOf (':');
			int port = Int32.Parse (ports.Substring (0, sp));
			int interrupt_port = Int32.Parse (ports.Substring (sp+1));
	
			Console.WriteLine ("csharp-agent: started, connecting to localhost:" + port);
	
			TcpClient client = new TcpClient ("127.0.0.1", port);
			TcpClient interrupt_client = new TcpClient ("127.0.0.1", interrupt_port);
			Console.WriteLine ("csharp-agent: connected.");
	
			NetworkStream s = client.GetStream ();
			interrupt_stream = interrupt_client.GetStream ();
			new Thread (InterruptListener).Start ();

			try {
				Evaluator.Init (new string [0]);
			} catch {
				// TODO: send a result back.
				Console.WriteLine ("csharp-agent: initialization failed");
				return;
			}
	
			try {
				// Add all assemblies loaded later
				AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
	
				// Add all currently loaded assemblies
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies ())
					Evaluator.ReferenceAssembly (a);
	
				RunRepl (s);
			} finally {
				AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
				client.Close ();
				interrupt_client.Close ();
				Console.WriteLine ("csharp-agent: disconnected.");			
			}
		}
	
		static void AssemblyLoaded (object sender, AssemblyLoadEventArgs e)
		{
			Evaluator.ReferenceAssembly (e.LoadedAssembly);
		}
	
		public void RunRepl (NetworkStream s)
		{
			string input = null;

			while (!InteractiveBase.QuitRequested) {
				try {
					string error_string;
					StringWriter error_output = new StringWriter ();
					Report.Stderr = error_output;
					
					string line = s.GetString ();
	
					bool result_set;
					object result;
	
					if (input == null)
						input = line;
					else
						input = input + "\n" + line;
	
					try {
						input = Evaluator.Evaluate (input, out result, out result_set);
					} catch (Exception e) {
						s.WriteByte ((byte) AgentStatus.ERROR);
						s.WriteString (e.ToString ());
						s.WriteByte ((byte) AgentStatus.RESULT_NOT_SET);
						continue;
					}
					
					if (input != null){
						s.WriteByte ((byte) AgentStatus.PARTIAL_INPUT);
						continue;
					}
	
					// Send warnings and errors back
					error_string = error_output.ToString ();
					if (error_string.Length != 0){
						s.WriteByte ((byte) AgentStatus.ERROR);
						s.WriteString (error_output.ToString ());
					}
	
					if (result_set){
						s.WriteByte ((byte) AgentStatus.RESULT_SET);
						StringWriter sr = new StringWriter ();
						CSharpShell.PrettyPrint (sr, result);
						s.WriteString (sr.ToString ());
					} else {
						s.WriteByte ((byte) AgentStatus.RESULT_NOT_SET);
					}
				} catch (IOException) {
					break;
				} catch (Exception e){
					Console.WriteLine (e);
				}
			}
		}
	}

	public class UnixUtils {
		[System.Runtime.InteropServices.DllImport ("libc", EntryPoint="isatty")]
		extern static int _isatty (int fd);
			
		public static bool isatty (int fd)
		{
			try {
				return _isatty (fd) == 1;
			} catch {
				return false;
			}
		}
	}
}
	
namespace Mono.Management
{
	interface IVirtualMachine {
		void LoadAgent (string filename, string args);
	}
}


