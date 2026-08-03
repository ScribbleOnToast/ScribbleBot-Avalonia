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
            bool success = false;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                System.IO.File.WriteAllLines(filePath, content);


                //TODO Add some kind of verification that the file was written successfully,
                //maybe check if the file exists and has the expected number of lines or something similar.
                //Maybe iterate through content and check if each line exists in the file, but that might be overkill.
                var validate = true;
                success = validate;
            }
            catch
            {
                success = false;
            }
            return success;
            
        }

        public bool ModifyFile(string filePath, int lineNumber, string[] content) 
        {
            var success = false;
            try
            {
                var lines = ReadFile(filePath);
                lines.InsertRange(lineNumber, content);
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
