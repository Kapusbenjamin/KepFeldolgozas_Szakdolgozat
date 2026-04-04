using .Data;
using .Data.Enums.Framework;

namespace .Helpers
{
    public static class Logger
    {
        /// <summary>
        /// Log exception, failure and messages into the database.
        /// If database connection not exists FileLog function will be called.
        /// </summary>
        /// <param name="path">Endpoint from the exception</param>
        /// <param name="eventType">Type of the event connected to the log</param>
        /// <param name="message">Message to be saved</param>
        /// <param name="userId">Loged in user identity</param>
        public static void Log(DatabaseContext db, string path, EventType eventType, string message, int? userId = null)
        {
            try
            {
                db.Logs.Add(new Data.Entities.Framework.Log()
                {
                    EventType = eventType,
                    UserId = userId,
                    InsertDate = DateTime.Now,
                    Path = path,
                    Value = message
                });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                FileLog(ex.Message, path);
            }
        }

        public static void FileLog(string fileName, string content, bool overwrite = false)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(basePath, "logs");
            var path = Path.Combine(logDir, fileName);

            try
            {

                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                if (!File.Exists(path) || overwrite)
                {
                    using (StreamWriter sw = File.CreateText(path))
                    {
                        sw.Write(content);
                    }
                }
                else
                {
                    using (StreamWriter sw = File.AppendText(path))
                    {
                        sw.Write(content);
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// In case if database connection not exists. 
        /// If file write permission not exists message will be dropped.
        /// </summary>
        /// <param name="msg">Message to be saved</param>
        public static void FileLog(string msg, string source)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(basePath, "logs");
            var fileName = $"{DateTime.Now:yyyyMMdd}.log";
            var path = Path.Combine(logDir, fileName);

            try
            {

                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                if (!File.Exists(path))
                {
                    using (StreamWriter sw = File.CreateText(path))
                    {
                        sw.WriteLine($"{source} - {DateTime.Now:yyyy.MM.dd HH:mm:ss.fff} >>> {msg}");
                    }
                }
                else
                {
                    using (StreamWriter sw = File.AppendText(path))
                    {
                        sw.WriteLine($"{source} - {DateTime.Now:yyyy.MM.dd HH:mm:ss.fff} >>> {msg}");
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
