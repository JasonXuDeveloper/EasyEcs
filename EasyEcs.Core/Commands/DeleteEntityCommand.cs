namespace EasyEcs.Core.Commands;

internal struct DeleteEntityCommand : ICommand
{
    public readonly int Id;

    public DeleteEntityCommand()
    {
        Id = -1;
    }

    public DeleteEntityCommand(int id)
    {
        Id = id;
    }
}