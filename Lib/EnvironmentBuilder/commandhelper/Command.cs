namespace EnvironmentBuilder.CommandSupporter
{
    public class Command
    {
        public string CommandName { get; }
        public string[] Arguments { get; }

        public Command(string commandName, params string[] arguments)
        {
            CommandName = commandName;
            Arguments = arguments;
        }
    }
}