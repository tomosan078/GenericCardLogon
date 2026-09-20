using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GenericCardLogon.Core
{
    public sealed class RegistrationStore
    {
        private static readonly object SyncRoot = new object();
        private readonly string _filePath;

        public RegistrationStore() : this(GetDefaultPath()) { }

        public RegistrationStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));
            _filePath = filePath;
        }

        public static string GetDefaultPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GenericCardLogon");
            return Path.Combine(dir, "cards.json");
        }

        public IReadOnlyList<CardRegistration> GetAll()
        {
            lock (SyncRoot)
            {
                return LoadUnsafe().ToList().AsReadOnly();
            }
        }

        public void Add(CardRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (string.IsNullOrWhiteSpace(registration.IdmHash)) throw new ArgumentException("IdmHash is required.");
            if (string.IsNullOrWhiteSpace(registration.UserName)) throw new ArgumentException("UserName is required.");

            lock (SyncRoot)
            {
                var items = LoadUnsafe();
                if (items.Any(x => string.Equals(x.IdmHash, registration.IdmHash, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("このIDm Hashはすでに登録されています。");

                items.Add(registration);
                SaveUnsafe(items);
            }
        }

        public bool Delete(string idmHash)
        {
            if (string.IsNullOrWhiteSpace(idmHash)) return false;

            lock (SyncRoot)
            {
                var items = LoadUnsafe();
                var removed = items.RemoveAll(x => string.Equals(x.IdmHash, idmHash, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return false;
                SaveUnsafe(items);
                return true;
            }
        }

        public void Backup(string destinationFile)
        {
            if (string.IsNullOrWhiteSpace(destinationFile))
                throw new ArgumentException("Destination file is required.", nameof(destinationFile));

            lock (SyncRoot)
            {
                if (!File.Exists(_filePath))
                    throw new FileNotFoundException("登録DBがまだ存在しません。", _filePath);

                var destinationDir = Path.GetDirectoryName(destinationFile);
                if (!string.IsNullOrWhiteSpace(destinationDir)) Directory.CreateDirectory(destinationDir);
                File.Copy(_filePath, destinationFile, true);
            }
        }

        private List<CardRegistration> LoadUnsafe()
        {
            if (!File.Exists(_filePath)) return new List<CardRegistration>();

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new List<CardRegistration>();

            var serializer = new DataContractJsonSerializer(typeof(List<CardRegistration>));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (List<CardRegistration>)serializer.ReadObject(stream) ?? new List<CardRegistration>();
            }
        }

        private void SaveUnsafe(List<CardRegistration> items)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var serializer = new DataContractJsonSerializer(typeof(List<CardRegistration>));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, items);
                var json = Encoding.UTF8.GetString(stream.ToArray());
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(_filePath)) File.Replace(tempPath, _filePath, null);
                else File.Move(tempPath, _filePath);
            }
        }
    }
}
