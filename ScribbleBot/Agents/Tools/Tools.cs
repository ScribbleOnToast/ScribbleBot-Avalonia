using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ScribbleBot.Agents.Tools
{
    public class ToolsForCodeWorker
    {
        private readonly ToolDispatcher _toolDispatcher;
        public ToolsForCodeWorker(ToolDispatcher toolDispatcher)
        {
            _toolDispatcher = toolDispatcher;
        }

        public List<AITool> AvailableTools()
        {
            List<AITool> availableTools = new List<AITool>
            {
                 AIFunctionFactory.Create(
                    (string folderPath) => _toolDispatcher.DispatchAsync("index_codebase", JsonSerializer.Serialize(new { folderPath })),
                   "index_codebase",
                    "Scans and indexes all .cs, .xaml, .json, and config files in the target directory into the SQLite structural map. Call this when given a folder path to consume."
                    ),

                AIFunctionFactory.Create(
                    (string query) => _toolDispatcher.DispatchAsync("search_code_symbols", JsonSerializer.Serialize(new { query })),
                    "search_code_symbols",
                    "Searches the SQLite FTS index for classes, methods, and signatures across the indexed codebase. Use for exact symbol name searches."),

                AIFunctionFactory.Create(
                    (string projectName, string query) => _toolDispatcher.DispatchAsync("search_code_semantic", JsonSerializer.Serialize(new { projectName, query })),
                    "search_code_semantic",
                    "Performs semantic (meaning-based) search across the indexed codebase using embeddings. Use when the user asks 'how does X work' or 'where is the code that handles Y' — finds relevant symbols even when exact names don't match. Requires a project to be indexed first."
                    ),

                AIFunctionFactory.Create(
                    (string projectName) => _toolDispatcher.DispatchAsync("get_project_summary", JsonSerializer.Serialize(new { projectName })),
                    "get_project_summary",
                    "Retrieves high-level architectural overview and primary types for an indexed project."
                    ),

                AIFunctionFactory.Create(
                    (string projectName, string symbolIdentifier) => _toolDispatcher.DispatchAsync("get_symbol_content", JsonSerializer.Serialize(new { projectName, symbolIdentifier })),
                    "get_symbol_content",
                    "Fetches the exact source code content and line numbers for a specific class, method, or file."
                    ),

                AIFunctionFactory.Create(
                    (string symbolIdentifier) => _toolDispatcher.DispatchAsync("get_symbol_relationships", JsonSerializer.Serialize(new { symbolIdentifier })),
                    "get_symbol_relationships",
                    "Retrieves call graphs, interface implementations, and dependencies for a target symbol."
                    ),

                AIFunctionFactory.Create(
                    () => _toolDispatcher.DispatchAsync("list_indexed_projects", "{}"),
                    "list_indexed_projects",
                    "Retrieve a list of index projects by name"
                    ),

                AIFunctionFactory.Create((string filePath) => _toolDispatcher.DispatchAsync("read_file", JsonSerializer.Serialize(new { filePath })),
                    "read_file",
                    "Reads the content of a file from the local filesystem. Use this to read any file that is not part of the indexed codebase."
                    ),

                AIFunctionFactory.Create((string filePath, string[] content) => _toolDispatcher.DispatchAsync("write_file", JsonSerializer.Serialize(new { filePath, content })),
                    "write_file",
                    "Write a file to the local filesystem, line by line. Use this to write a NEW TEXT BASED FILE. It should not be used to update or modify existing files."
                    ),

                AIFunctionFactory.Create((string filePath, int lineNumber, string[] newContent) => _toolDispatcher.DispatchAsync("modify_file", JsonSerializer.Serialize(new { filePath, lineNumber, newContent })),
                    "modify_file",
                    "Update to modify a file to insert new lines after a specific line in a text file on the local filesystem. Use this to modify existing files. Do not attempt to remove existing lines."
                    )
            };

            return availableTools;
        }
    }
}