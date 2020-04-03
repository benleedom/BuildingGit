using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using zlib;

namespace BuildingGit
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("fatal: command required");
                Environment.Exit(1);
            }

            string command = args[0];
            switch (command)
            {
                case "init":
                    try
                    {
                        string repositoryPath = args.Length >= 2 ? args[1] : Environment.CurrentDirectory;
                        repositoryPath = Path.GetFullPath(repositoryPath);
                        string gitPath = Path.Combine(repositoryPath, ".git");
                        foreach (string folder in new string[] { "objects", "refs" })
                        {
                            Directory.CreateDirectory(Path.Combine(gitPath, folder));
                        }
                        Console.WriteLine("Initialized empty Jit repository in " + gitPath);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("fatal: " + e.Message);
                    }
                    break;
                case "commit":
                    {
                        try
                        {
                            string repositoryPath = Environment.CurrentDirectory;
                            string gitPath = Path.Combine(repositoryPath, ".git");
                            string databasePath = Path.Combine(gitPath, "objects");
                            var database = new Database(databasePath);
                            var entries = new List<TreeEntry>();
                            foreach (string path in new Workspace(repositoryPath).ListFiles())
                            {
                                string fullPath = Path.Combine(repositoryPath, path);
                                var blob = new Blob(Encoding.ASCII.GetBytes(File.ReadAllText(fullPath)));
                                database.Store(blob);
                                entries.Add(new TreeEntry(path, blob.ObjectId));
                            }
                            var tree = new Tree(entries);
                            database.Store(tree);

                            string authorName = Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME");
                            string authorEmail = Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL");
                            var author = new Author(authorName, authorEmail, DateTime.Now);
                            string message = Console.In.ReadToEnd();

                            var commit = new Commit(tree.ObjectId, author, message);
                            database.Store(commit);

                            File.WriteAllText(Path.Combine(gitPath, "HEAD"), commit.ObjectId);
                            Console.WriteLine($"[(root-commit) {commit.ObjectId}]\n{message}");
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine("fatal: " + e.Message);
                        }
                    }
                    break;
                default:
                    Console.Error.WriteLine(command + " is not a jit command.");
                    Environment.Exit(1);
                    break;
            }
        }
    }

    internal sealed class Workspace
    {
        private static string _rootPath;

        public Workspace(string rootPath)
        {
            _rootPath = rootPath;
        }

        public IEnumerable<string> ListFiles() => Directory.EnumerateFiles(_rootPath).Select(p => p.Substring(_rootPath.Length + 1));

        public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(_rootPath, relativePath));
    }

    internal abstract class DatabaseObject
    {
        public abstract string Type { get; }

        public abstract byte[] Content { get; }

        public string ObjectId { get; set; }
    }

    internal sealed class Blob : DatabaseObject
    {
        private readonly byte[] _data;

        public Blob(byte[] data)
        {
            _data = data;
        }

        public override string Type => "blob";

        public override byte[] Content => _data;
    }

    internal sealed class Database
    {
        private readonly string _databasePath;

        public Database(string databasePath)
        {
            _databasePath = databasePath;
        }

        public void Store(DatabaseObject obj)
        {
            byte[] asciiContentString = obj.Content;
            var contentBytes = new List<byte>();
            contentBytes.AddAsciiBytes($"{obj.Type} {asciiContentString.Length}");
            contentBytes.Add(0);
            contentBytes.AddRange(asciiContentString);
            byte[] contentBytesArray = contentBytes.ToArray();

            byte[] contentHash;
            using (var sha1Hash = SHA1.Create())
            {
                contentHash = sha1Hash.ComputeHash(contentBytesArray);
            }

            obj.ObjectId = Utils.BytesToHex(contentHash);
            WriteObject(obj.ObjectId, contentBytesArray);
        }

        private void WriteObject(string objectHashHex, byte[] objectData)
        {
            string firstFolder = objectHashHex.Substring(0, 2),
                secondFolder = objectHashHex.Substring(2);
            string objectFolder = Path.Combine(_databasePath, firstFolder);
            Directory.CreateDirectory(objectFolder);

            string objectPath = Path.Combine(objectFolder, secondFolder);
            if (File.Exists(objectPath))
            {
                return;
            }

            string temporaryFilePath = Path.Combine(objectFolder, Path.GetRandomFileName());

            using (Stream temporaryFileStream = File.OpenWrite(temporaryFilePath))
            using (var deflateStream = new ZOutputStream(temporaryFileStream, zlibConst.Z_BEST_SPEED))
            {
                deflateStream.Write(objectData, 0, objectData.Length);
            }
            File.Move(temporaryFilePath, objectPath);
        }
    }

    internal sealed class TreeEntry
    {
        public TreeEntry(string path, string objectId)
        {
            Path = path;
            ObjectId = objectId;
        }

        public string Path { get; }

        public string ObjectId { get; }
    }

    internal sealed class Tree : DatabaseObject
    {
        private readonly TreeEntry[] _entries;

        public Tree(IEnumerable<TreeEntry> entries)
        {
            _entries = entries.ToArray();
        }

        public override string Type => "tree";

        public override byte[] Content => _entries.OrderBy(e => e.Path, StringComparer.Ordinal).SelectMany(EncodeEntry).ToArray();

        private static IEnumerable<byte> EncodeEntry(TreeEntry entry)
        {
            List<byte> encodedBytes = new List<byte>();
            encodedBytes.AddAsciiBytes("100644".PadRight(7));
            encodedBytes.AddAsciiBytes(entry.Path);
            encodedBytes.Add(0);
            encodedBytes.AddRange(Utils.HexToBytes(entry.ObjectId));
            return encodedBytes;
        }
    }

    internal sealed class Author
    {
        public Author(string name, string email, DateTime time)
        {
            Name = name;
            Email = email;
            Time = time;
        }

        public string Name { get; }

        public string Email { get; }

        public DateTime Time { get; }

        private string TimeStamp
        {
            get
            {
                int seconds = (int)Time.Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;
                return $"{seconds} +0000";
            }
        }

        public override string ToString() => $"{Name} <{Email}> {TimeStamp}";
    }

    internal sealed class Commit : DatabaseObject
    {
        private readonly string _treeObjectId, _message;
        private readonly Author _author;

        public Commit(string treeObjectId, Author author, string message)
        {
            _treeObjectId = treeObjectId;
            _author = author;
            _message = message;
        }

        public override string Type => "commit";

        public override byte[] Content
        {
            get
            {
                string author = _author.ToString();
                return Encoding.ASCII.GetBytes($"tree {_treeObjectId}\nauthor {author}\ncommitter {author}\n\n{_message}");
            }
        }
    }

    internal static class Utils
    {
        public static string BytesToHex(byte[] data)
        {
            return new string(data.SelectMany(b => b.ToString("x2").ToCharArray()).ToArray());
        }

        public static byte[] HexToBytes(string data)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < data.Length; i += 2)
            {
                bytes.Add(byte.Parse(data.Substring(i, 2), NumberStyles.HexNumber));
            }
            return bytes.ToArray();
        }

        public static void AddAsciiBytes(this List<byte> byteList, string str) => byteList.AddRange(Encoding.ASCII.GetBytes(str));
    }
}
