using System.Collections.Concurrent;

namespace EasyEcs.Core.Commands;

internal class CommandBuffer
{
    private readonly ConcurrentQueue<ICommand> _commands = new();
    
    public void AddCommand(ICommand command)
    {
        _commands.Enqueue(command);
    }
    
    public bool TryGetCommand(out ICommand command)
    {
        return _commands.TryDequeue(out command);
    }
}