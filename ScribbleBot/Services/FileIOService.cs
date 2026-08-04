using System;
using System.Collections.Generic;
using System.Text;

namespace ScribbleBot.Services
{
    public class FileIOService
    {
        public List<string> ReadFile(string filePath)
        {
            using var reader = new System.IO.StreamReader(filePath);
            List<string> lines = new();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            return lines;
        }

        public bool WriteFile(string filePath, string[] content) 
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                System.IO.File.WriteAllLines(filePath, content);
                                
                return File.Exists(filePath) && ReadFile(filePath).Count == content.Length;
            }
            catch
            {
                return false;
            }
        }

        public bool ModifyFile(string filePath, int lineNumber, string[] content) 
        {
            var success = false;
            try
            {
                var lines = ReadFile(filePath);
                lines.InsertRange(lineNumber + 1, content);
                success = WriteFile(filePath, lines.ToArray());
            }
            catch
            {
                success = false;
            }
            return success;
        }
    }
}
