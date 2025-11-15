using System;
using System.Text;

namespace ProcessLauncher.CommandLineArgumentLibrary
{
    public class ArgumentBuilder
    {
        private StringBuilder argumentBuilder;
        private string baseCommand;
        private string mainArgument;

        public ArgumentBuilder()
        {
            argumentBuilder = new StringBuilder();
        }

        // Method to set the base command (e.g., .\EnvironmentManager.exe)
        public ArgumentBuilder SetBaseCommand(string command)
        {
            baseCommand = command;
            return this;
        }

        // Method to set the main argument (e.g., SQLServer)
        public ArgumentBuilder SetMainArgument(string argument)
        {
            mainArgument = argument;
            return this;
        }

        public ArgumentBuilder AddStringParameter(string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                argumentBuilder.AppendFormat("--{0} \"{1}\" ", name, value);
            }
            return this;
        }

        public ArgumentBuilder AddIntParameter(string name, int value)
        {
            argumentBuilder.AppendFormat("--{0} {1} ", name, value);
            return this;
        }

        public ArgumentBuilder AddFlag(string name, bool value)
        {
            if (value)
            {
                argumentBuilder.AppendFormat("--{0} ", name);
            }
            return this;
        }
        public ArgumentBuilder AddStringArrayParameter(string name, string[] values)
        {
            if (values != null && values.Length > 0)
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        argumentBuilder.AppendFormat("--{0} \"{1}\" ", name, value);
                    }
                }
            }
            return this;
        }
        public ArgumentBuilder AddTag(string tagName, string tagValue = null)
        {
            if (!string.IsNullOrEmpty(tagName))
            {
                if (!string.IsNullOrEmpty(tagValue))
                {
                    argumentBuilder.AppendFormat("-{0} \"{1}\" ", tagName, tagValue);
                }
                else
                {
                    argumentBuilder.AppendFormat("-{0} ", tagName);
                }
            }
            return this;
        }
        public string Build()
        {
            // Construct the full command
            var fullCommand = new StringBuilder();
            if (!string.IsNullOrEmpty(baseCommand))
            {
                fullCommand.Append(baseCommand);
            }

            if (!string.IsNullOrEmpty(mainArgument))
            {
                fullCommand.Append($" {mainArgument}");
            }

            fullCommand.Append($" {argumentBuilder.ToString().Trim()}");

            return fullCommand.ToString().Trim();
        }
    }
}
