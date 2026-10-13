using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Assam.Core.Learning
{
    /// <summary>
    /// 負責把學習記憶讀寫到本機磁碟（不使用網路、不使用資料庫，只有一個 JSON 檔）。
    /// 寫入時先寫暫存檔再取代，斷電也不會把舊記憶寫壞。
    /// </summary>
    public sealed class MemoryStore
    {
        public const string MemoryFileName = "memory.json";
        public const int MaxHistory = 300;
        public const int MaxSamples = 500;

        public MemoryStore(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) throw new ArgumentNullException("dataDirectory");
            DataDirectory = dataDirectory;
            Data = UserMemoryData.CreateNew();
        }

        public string DataDirectory { get; private set; }

        public string MemoryFilePath
        {
            get { return Path.Combine(DataDirectory, MemoryFileName); }
        }

        /// <summary>使用者自訂知識檔（*.md）放置的資料夾。</summary>
        public string KnowledgeDirectory
        {
            get { return Path.Combine(DataDirectory, "Knowledge"); }
        }

        public UserMemoryData Data { get; private set; }

        public string LastMessage { get; private set; }

        /// <summary>
        /// 預設資料夾：優先使用程式所在目錄下的 UserData（可攜式，隨身碟也能帶著走）；
        /// 若沒有寫入權限（例如裝在 Program Files），改用 %LocalAppData%\ASSAM。
        /// </summary>
        public static string ResolveDefaultDirectory(string appBaseDirectory)
        {
            string portable = Path.Combine(appBaseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "UserData");
            if (IsWritable(portable)) return portable;
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASSAM");
            Directory.CreateDirectory(local);
            return local;
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write_test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Load()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(KnowledgeDirectory);
            string pending = MemoryFilePath + ".tmp";
            if (!File.Exists(MemoryFilePath) && File.Exists(pending))
                File.Move(pending, MemoryFilePath); // 上次存檔在最後一步中斷，補完它
            if (!File.Exists(MemoryFilePath))
            {
                Data = UserMemoryData.CreateNew();
                LastMessage = "建立新的學習記憶：" + MemoryFilePath;
                return;
            }

            try
            {
                Data = Deserialize(File.ReadAllBytes(MemoryFilePath));
                LastMessage = "已載入學習記憶：" + MemoryFilePath;
            }
            catch (Exception ex)
            {
                // 記憶檔損毀時保留原檔以便手動救回，並以全新記憶繼續執行
                string broken = Path.Combine(DataDirectory, "memory.broken-" + Stamp() + ".json");
                try { File.Copy(MemoryFilePath, broken, true); } catch (Exception) { }
                Data = UserMemoryData.CreateNew();
                LastMessage = "記憶檔無法讀取（" + ex.Message + "），已另存為 " + Path.GetFileName(broken) + " 並建立新記憶。";
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            TrimCollections();
            Data.UpdatedAt = UserMemoryData.Now();
            byte[] bytes = Serialize(Data);

            string temp = MemoryFilePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(MemoryFilePath))
            {
                string backup = MemoryFilePath + ".bak";
                File.Copy(MemoryFilePath, backup, true);
                File.Delete(MemoryFilePath);
            }
            File.Move(temp, MemoryFilePath);
        }

        /// <summary>清除所有學習記憶（會先備份）。</summary>
        public string Reset()
        {
            string backup = null;
            if (File.Exists(MemoryFilePath))
            {
                backup = Path.Combine(DataDirectory, "memory.reset-" + Stamp() + ".json");
                File.Copy(MemoryFilePath, backup, true);
            }
            Data = UserMemoryData.CreateNew();
            Save();
            return backup;
        }

        public void ExportTo(string path)
        {
            File.WriteAllBytes(path, Serialize(Data));
        }

        /// <summary>匯入記憶檔（會取代目前記憶，原記憶先備份）。</summary>
        public void ImportFrom(string path)
        {
            var imported = Deserialize(File.ReadAllBytes(path));
            if (File.Exists(MemoryFilePath))
                File.Copy(MemoryFilePath, Path.Combine(DataDirectory, "memory.before-import-" + Stamp() + ".json"), true);
            Data = imported;
            Save();
        }

        public void AddHistory(HistoryItem item)
        {
            Data.History.Add(item);
            if (Data.History.Count > MaxHistory)
                Data.History.RemoveRange(0, Data.History.Count - MaxHistory);
        }

        private void TrimCollections()
        {
            if (Data.History.Count > MaxHistory) Data.History.RemoveRange(0, Data.History.Count - MaxHistory);
            if (Data.ArchSamples.Count > MaxSamples) Data.ArchSamples.RemoveRange(0, Data.ArchSamples.Count - MaxSamples);
            if (Data.IntentSamples.Count > MaxSamples) Data.IntentSamples.RemoveRange(0, Data.IntentSamples.Count - MaxSamples);
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(typeof(UserMemoryData), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        }

        public static byte[] Serialize(UserMemoryData data)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    CreateSerializer().WriteObject(writer, data);
                    writer.Flush();
                }
                return ms.ToArray();
            }
        }

        public static UserMemoryData Deserialize(byte[] bytes)
        {
            // 允許檔案開頭有 UTF-8 BOM（使用者用記事本編輯過時常見）
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using (var ms = new MemoryStream(bytes, offset, bytes.Length - offset))
            {
                var data = (UserMemoryData)CreateSerializer().ReadObject(ms);
                if (data == null) throw new InvalidDataException("記憶檔內容是空的。");
                data.EnsureInitialized();
                return data;
            }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
