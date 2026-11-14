namespace MonRem.Host.Video.Wire;

/// <summary>Primeiro inteiro do cabeçalho (SPEC 7.1).</summary>
public enum MessageType : uint
{
    Identification = 0,
    Ping = 1,
    Framebuffer = 2,
    Visibility = 3,
    DisplaySettings = 4,
    EvtDisplaySettings = 6,
    FlowControlAck = 7,
    Error = 8,
    Rotation = 9,
    InputMouse = 10,
    InputKeyboard = 11,
    InputTouch = 12,
    InputPen = 13,
    AudioSpeaker = 14,
}
