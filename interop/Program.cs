using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;
using Newtonsoft.Json;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace interop {
    class Program {
        const Int32 SW_MINIMIZE = 6;

        [DllImport("Kernel32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow([In] IntPtr hWnd, [In] Int32 nCmdShow);

        private static int UserId = -1;
        private static object IOLock = new object();
        private static List<Message> Logs = new List<Message>();
        private static Dictionary<int, string> Users = new Dictionary<int, string>();
        private static WebSocket Sock;
        private static bool Shown = true;
        private static Timer pingTimer;

        private static Dictionary<string, string> BotDefs = new Dictionary<string, string>();
        private static Dictionary<string, string> BotErrorDefs = new Dictionary<string, string>();

        private static string Username;
        private static string Password;

        static void MoveCursor(int x, int y) {
            Console.CursorLeft = x;
            Console.CursorTop = y + (Shown ? 0 : Console.WindowHeight);
        }

        static string Pack(int id, params string[] data) {
            var newData = data.ToList();
            newData.Insert(0, id.ToString());
            return String.Join("\t", newData);
        }

        static Packet Unpack(string data) {
            var parts = data.Split('\t');
            return new Packet {
                Id = Int32.Parse(parts[0]),
                Data = parts.Skip(1).ToList()
            };
        }

        static string ShortenName(string name) {
            return name.Length < 8 ? name.PadLeft(8, ' ') : name.Substring(0, 8);
        }

        static int GetLineCount(Message message) {
            var lines = message.Contents.Split(new[] { "\r\n" }, StringSplitOptions.None);
            int lineCount = 0;
            for(int i = 0; i < lines.Length; ++i) {
                int lineLength = lines[i].Length + (i == 0 ? 15 : 0);
                lineCount += (int)Math.Ceiling((double)lineLength / (double)Console.WindowWidth);
            }

            return lineCount + (lines.Length > 1 ? 1 : 0);
        }

        static void CorrectMessage(ref Message message) {
            message.Sender = ShortenName(message.Sender);
            message.Contents = Regex.Replace(message.Contents, @"\[.*?\]", "");
            message.Contents = message.Contents.Replace("\r", "");
            message.Contents = message.Contents.Replace("<br/>", "\r\n");
            message.Contents = message.Contents.Replace("&lt;", "<");
            message.Contents = message.Contents.Replace("&gt;", ">");
        }

        static void LoadLanguageFile(string raw) {
            var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(raw);

            foreach(var pair in data["botText"])
                BotDefs.Add(pair.Key, pair.Value);
            foreach(var pair in data["botErrText"])
                BotErrorDefs.Add(pair.Key, pair.Value);
        }

        static string ParseBotMessage(string raw) {
            var parts = raw.Split('\f');

            bool isErr = parts[0] == "1";
            var outMsg = raw;
            if((isErr ? BotErrorDefs : BotDefs).ContainsKey(parts[1])) {
                outMsg = (isErr ? BotErrorDefs : BotDefs)[parts[1]];
                for(int i = 2; i < parts.Length; ++i)
                    outMsg = outMsg.Replace("{"+ (i - 2) +"}", parts[i]);
            }

            return outMsg;
        }

        static void WriteMessage(Message message) {
            CorrectMessage(ref message);

            lock(IOLock) {
                int cx = Console.CursorLeft, cy = Console.CursorTop;

                var lineCount = GetLineCount(message);
                if(lineCount > Console.WindowHeight - 3)
                    message.Contents = "This message was too long to show.";

                if(Shown)
                    Console.MoveBufferArea(0, lineCount, Console.WindowWidth, Console.WindowHeight - lineCount - 3, 0, 0);
                else
                    Console.MoveBufferArea(0, Console.WindowHeight + lineCount, Console.WindowWidth, Console.WindowHeight - lineCount - 3, 0, Console.WindowHeight);
                MoveCursor(0, Console.WindowHeight - 3 - lineCount);

                Console.Write(message.Sent.ToString("HHmm"));
                Console.Write(" ");
                Console.Write(message.Sender);
                Console.Write(": ");
                Console.Write(message.Contents);
                
                Console.CursorLeft = cx;
                Console.CursorTop = cy;
            }
        }

        static string HashToString(byte[] hash) {
            var sb = new StringBuilder(hash.Length * 2);
            foreach(var b in hash)
                sb.Append(b.ToString("x2"));

            return sb.ToString();
        }

        static void Main(string[] args) {
            Console.Write("Enter username: ");
            Username = Console.ReadLine();

            using(var sha = new SHA1Managed()) {
                Console.Write("Enter password: ");
                Password = HashToString(sha.ComputeHash(Encoding.UTF8.GetBytes(HashToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Console.ReadLine()))))));
            }

            LoadLanguageFile(Properties.Resources.CommonLang);
            LoadLanguageFile(Properties.Resources.CoreLang);

            Console.CursorLeft = 0;
            Console.CursorTop = Console.WindowHeight - 3;
            Console.Write(new String('~', Console.WindowWidth));
            Console.Write("> ");

            Console.SetBufferSize(Console.WindowWidth, Console.WindowHeight * 2);
            using(Sock = new WebSocket(Properties.Resources.ConnectionAddress)) {
                Users.Add(-1, "ChatBot");
                Sock.OnOpen += OnOpen;
                Sock.OnMessage += OnMessage;
                Sock.OnClose += Sock_OnClose;
                Sock.Connect();

                string message = "";
                while(true) {
                    var key = Console.ReadKey(true);

                    lock(IOLock) {
                        switch(key.KeyChar) {
                            case '\x08':
                                if(!Shown)
                                    break;
                                if(message.Length == 0)
                                    break;

                                message = message.Substring(0, message.Length - 1);
                                if(Console.CursorLeft == 0) {
                                    Console.CursorTop--;
                                    Console.CursorLeft = Console.WindowWidth;
                                    Console.Write(' ');

                                    Console.CursorTop--;
                                    Console.CursorLeft = Console.WindowWidth;
                                } else {
                                    Console.CursorLeft--;
                                    Console.Write(' ');
                                    Console.CursorLeft--;
                                }
                                break;
                            case '\x0A':
                            case '\x0D':
                                if(!Shown)
                                    break;

                                Console.CursorTop = Console.WindowHeight - 2;
                                Console.CursorLeft = 0;

                                Console.Write(">");
                                Console.Write(new String(' ', Console.WindowWidth * 2 - 5));
                                Console.CursorTop = Console.WindowHeight - 2;
                                Console.CursorLeft = 2;

                                //message = message.Trim() + " de geso~";
                                Sock.Send(Pack(2, UserId.ToString(), message));
                                message = "";
                                break;
                            case '\x1B':
                                Sock.Send(Pack(2, UserId.ToString(), "brb (auto)"));
                                var hwndCli = GetConsoleWindow();
                                ShowWindow(hwndCli, SW_MINIMIZE);
                                break;
                            default:
                                if(!Shown)
                                    break;

                                message += key.KeyChar;
                                Console.Write(key.KeyChar);
                                break;
                        }
                    }
                }

                Sock.Close();
            }
        }

        private static void Sock_OnClose(object sender, CloseEventArgs e) {
            Environment.Exit(0);
        }

        static void OnOpen(object sender, EventArgs e) {
            Sock.Send(Pack(1, Username, Password));

            /*
#if DEBUG
            Sock.Send(Pack(1, Properties.Resources.ConnectionUserDebug, Properties.Resources.ConnectionPwd));
#else
            Sock.Send(Pack(1, Properties.Resources.ConnectionUserRelease, Properties.Resources.ConnectionPwd));
#endif
            */
        }

        private static void PingTimer_Elapsed(object sender, ElapsedEventArgs e) {
            Sock.Send(Pack(0, UserId.ToString()));
        }

        static void OnMessage(object sender, MessageEventArgs e) {
            var msg = Unpack(e.Data);
            Message message;

            switch(msg.Id) {
                case 1:
                    if(UserId == -1) {
                        UserId = Int32.Parse(msg.Data[1]);
                        Users.Add(UserId, msg.Data[2]);

                        pingTimer = new Timer(30000);
                        pingTimer.AutoReset = true;
                        pingTimer.Elapsed += PingTimer_Elapsed;
                        pingTimer.Enabled = true;
                    } else {
                        var userId = Int32.Parse(msg.Data[1]);
                        try {
                            Users.Add(userId, msg.Data[2]);
                        } catch { }

                        message = new Message {
                            Sent = DateTime.Now,
                            Sender = Users[-1],
                            Contents = $"{Users[userId]} joined."
                        };
                        Logs.Add(message);
                        WriteMessage(message);
                    }
                    break;
                case 2:
                    var userIdd = Int32.Parse(msg.Data[1]);

                    message = new Message {
                        Sent = DateTimeOffset.FromUnixTimeSeconds(Int64.Parse(msg.Data[0])).ToLocalTime().DateTime,
                        Sender = Users[userIdd],
                        Contents = userIdd == -1 
                            ? ParseBotMessage(msg.Data[2])
                            : msg.Data[2]
                    };

                    Logs.Add(message);
                    WriteMessage(message);
                    break;
                case 3:
                    message = new Message {
                        Sent = DateTime.Now,
                        Sender = Users[-1],
                        Contents = $"{msg.Data[1]} " + (msg.Data[2] == "leave" ? "left." : "was kicked.")
                    };

                    Logs.Add(message);
                    WriteMessage(message);
                    break;
                case 7:
                    if(msg.Data[0] == "0") {
                        int count = Int32.Parse(msg.Data[1]);

                        for(int i = 0; i < count; ++i) {
                            int ix = 2 + 5*i;
                            try {
                                Users.Add(Int32.Parse(msg.Data[ix]), msg.Data[ix + 1]);
                            } catch { }
                        }
                    } else {
                        message = new Message {
                            Sent = DateTimeOffset.FromUnixTimeSeconds(Int64.Parse(msg.Data[1])).ToLocalTime().DateTime,
                            Sender = msg.Data[3],
                            Contents = msg.Data[2] == "-1"
                                ? ParseBotMessage(msg.Data[6])
                                : msg.Data[6]
                        };

                        Logs.Add(message);
                        WriteMessage(message);
                    }
                    break;
                case 10:
                    Users[Int32.Parse(msg.Data[0])] = msg.Data[1];
                    break;
            }
        }
        
        class Message {
            public DateTime Sent { get; set; }
            public string Sender { get; set; }
            public string Contents { get; set; }
        }

        class Packet {
            public int Id { get; set; }
            public List<string> Data { get; set; }
        }
    }
}
