namespace DrammenMJKConfig
{
    public sealed class Menu
    {
        public const char EscKey = '\x1B';

        private readonly List<(char Command, string Description, Action Action)> _items;
        private readonly (char Command, string Description)? _quitOption;

        public Menu(List<(char Command, string Description, Action Action)> items,
                    (char Command, string Description)? quitOption = null)
        {
            _items = items
                .Select(i => (char.ToUpperInvariant(i.Command), i.Description, i.Action))
                .ToList();
            _quitOption = quitOption.HasValue
                ? (char.ToUpperInvariant(quitOption.Value.Command), quitOption.Value.Description)
                : null;
        }

        public void Run()
        {
            while (true)
            {
                Console.WriteLine();

                foreach (var item in _items)
                    Console.WriteLine($"  {item.Command} - {item.Description}");

                if (_quitOption.HasValue)
                    Console.WriteLine($"  {_quitOption.Value.Command} - {_quitOption.Value.Description}");
                else
                    Console.WriteLine("  Esc - Quit");

                Console.WriteLine();

                while (true)
                {
                    Console.Write("> ");
                    var key = Console.ReadKey(intercept: true);

                    if (key.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("Esc");
                        return;
                    }

                    char cmd = char.ToUpperInvariant(key.KeyChar);

                    if (_quitOption.HasValue && cmd == _quitOption.Value.Command)
                    {
                        Console.WriteLine(cmd);
                        return;
                    }

                    var match = _items.FirstOrDefault(i => i.Command == cmd);
                    if (match.Action is not null)
                    {
                        Console.WriteLine(cmd);
                        match.Action();
                        break;
                    }
                }
            }
        }
    }
}
