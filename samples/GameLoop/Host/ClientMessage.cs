namespace GameLoop.Host;

// What a client sends over the network. Messages are not components:
// ReceiveInputSystem turns each one into a request entity.
public enum MessageKind : byte
{
    Join,
    Move,
    Fire,
    Leave,
}

public readonly record struct ClientMessage(int Client, MessageKind Kind, float X = 0, float Y = 0);
