using System;
using System.Threading;
namespace Microsoft.FlightSimulator.SimConnect
{
    public enum SIMCONNECT_DATATYPE { INVALID, INT32, INT64, FLOAT32, FLOAT64, STRING8, STRING32, STRING64, STRING128, STRING256, STRING260, STRINGV }
    public enum SIMCONNECT_PERIOD { NEVER, ONCE, VISUAL_FRAME, SIM_FRAME, SECOND }
    [Flags] public enum SIMCONNECT_DATA_REQUEST_FLAG { DEFAULT = 0, CHANGED = 1, TAGGED = 2 }
    [Flags] public enum SIMCONNECT_DATA_SET_FLAG { DEFAULT = 0, TAGGED = 1 }
    [Flags] public enum SIMCONNECT_EVENT_FLAG { DEFAULT = 0, FAST_REPEAT_TIMER = 1, SLOW_REPEAT_TIMER = 2, GROUPID_IS_PRIORITY = 16 }
    public enum SIMCONNECT_EXCEPTION { NONE, ERROR, SIZE_MISMATCH, UNRECOGNIZED_ID, UNOPENED, VERSION_MISMATCH, TOO_MANY_GROUPS, NAME_UNRECOGNIZED, GET_INPUT_EVENT_FAILED, SET_INPUT_EVENT_FAILED }
    public enum SIMCONNECT_INPUT_EVENT_TYPE { DOUBLE, STRING }
    public class SIMCONNECT_RECV { public uint dwSize, dwVersion, dwID; }
    public class SIMCONNECT_RECV_OPEN : SIMCONNECT_RECV { public string szApplicationName; public uint dwApplicationVersionMajor, dwApplicationVersionMinor, dwApplicationBuildMajor, dwApplicationBuildMinor; }
    public class SIMCONNECT_RECV_EXCEPTION : SIMCONNECT_RECV { public uint dwException, dwSendID, dwIndex; }
    public class SIMCONNECT_RECV_SIMOBJECT_DATA : SIMCONNECT_RECV { public uint dwRequestID, dwObjectID, dwDefineID, dwFlags, dwentrynumber, dwoutof, dwDefineCount; public object[] dwData; }
    public class SIMCONNECT_RECV_EVENT : SIMCONNECT_RECV { public uint uGroupID, uEventID, dwData; }
    public class SIMCONNECT_RECV_EVENT_FILENAME : SIMCONNECT_RECV_EVENT { public string szFileName; public uint dwFlags; }
    public struct SIMCONNECT_INPUT_EVENT_DESCRIPTOR { public string Name; public ulong Hash; public SIMCONNECT_INPUT_EVENT_TYPE eType; }
    public class SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS : SIMCONNECT_RECV { public object[] rgData; }
    public class SIMCONNECT_RECV_GET_INPUT_EVENT : SIMCONNECT_RECV { public uint dwRequestID; public SIMCONNECT_INPUT_EVENT_TYPE eType; public object[] Value; }
    public class SIMCONNECT_RECV_SUBSCRIBE_INPUT_EVENT : SIMCONNECT_RECV { public ulong Hash; public SIMCONNECT_INPUT_EVENT_TYPE eType; public object[] Value; }
    public class SIMCONNECT_RECV_ENUMERATE_INPUT_EVENT_PARAMS : SIMCONNECT_RECV { public ulong Hash; public string Value; }
    public delegate void SimConnect_RecvOpenEventHandler(SimConnect sender, SIMCONNECT_RECV_OPEN data);
    public delegate void SimConnect_RecvQuitEventHandler(SimConnect sender, SIMCONNECT_RECV data);
    public delegate void SimConnect_RecvExceptionEventHandler(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data);
    public delegate void SimConnect_RecvSimobjectDataEventHandler(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data);
    public delegate void SimConnect_RecvEventEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT data);
    public delegate void SimConnect_RecvEventFilenameEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data);
    public delegate void SimConnect_RecvEnumerateInputEventsEventHandler(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS data);
    public delegate void SimConnect_RecvGetInputEventEventHandler(SimConnect sender, SIMCONNECT_RECV_GET_INPUT_EVENT data);
    public delegate void SimConnect_RecvSubscribeInputEventEventHandler(SimConnect sender, SIMCONNECT_RECV_SUBSCRIBE_INPUT_EVENT data);
    public delegate void SimConnect_RecvEnumerateInputEventParamsEventHandler(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_INPUT_EVENT_PARAMS data);
    public class SimConnect : IDisposable
    {
        public const uint SIMCONNECT_OBJECT_ID_USER = 0;
        public const uint SIMCONNECT_UNUSED = uint.MaxValue;
        public const uint SIMCONNECT_GROUP_PRIORITY_HIGHEST = 1;
        public struct InputEventString { public string value; }
        public SimConnect(string szName, IntPtr hWnd, uint UserEventWin32, WaitHandle hEventHandle, uint ConfigIndex) { }
        public event SimConnect_RecvOpenEventHandler OnRecvOpen;
        public event SimConnect_RecvQuitEventHandler OnRecvQuit;
        public event SimConnect_RecvExceptionEventHandler OnRecvException;
        public event SimConnect_RecvSimobjectDataEventHandler OnRecvSimobjectData;
        public event SimConnect_RecvEventEventHandler OnRecvEvent;
        public event SimConnect_RecvEventFilenameEventHandler OnRecvEventFilename;
        public event SimConnect_RecvEnumerateInputEventsEventHandler OnRecvEnumerateInputEvents;
        public event SimConnect_RecvGetInputEventEventHandler OnRecvGetInputEvent;
        public event SimConnect_RecvSubscribeInputEventEventHandler OnRecvSubscribeInputEvent;
        public event SimConnect_RecvEnumerateInputEventParamsEventHandler OnRecvEnumerateInputEventParams;
        public void ReceiveMessage() { }
        public void AddToDataDefinition(Enum DefineID, string DatumName, string UnitsName, SIMCONNECT_DATATYPE DatumType, float fEpsilon, uint DatumID) { }
        public void RegisterDataDefineStruct<T>(Enum dwID) { }
        public void RegisterStruct<RECV, T>(Enum dwID) { }
        public void ClearDataDefinition(Enum DefineID) { }
        public void RequestDataOnSimObject(Enum RequestID, Enum DefineID, uint ObjectID, SIMCONNECT_PERIOD Period, SIMCONNECT_DATA_REQUEST_FLAG Flags, uint origin, uint interval, uint limit) { }
        public void SetDataOnSimObject(Enum DefineID, uint ObjectID, SIMCONNECT_DATA_SET_FLAG Flags, object pDataSet) { }
        public void MapClientEventToSimEvent(Enum EventID, string EventName) { }
        public void TransmitClientEvent(uint ObjectID, Enum EventID, uint dwData, Enum GroupID, SIMCONNECT_EVENT_FLAG Flags) { }
        public void SubscribeToSystemEvent(Enum EventID, string SystemEventName) { }
        public void EnumerateInputEvents(Enum RequestID) { }
        public void GetInputEvent(Enum RequestID, ulong Hash) { }
        public void SetInputEvent(ulong Hash, object Value) { }
        public void SubscribeInputEvent(ulong Hash) { }
        public void UnsubscribeInputEvent(ulong Hash) { }
        public void EnumerateInputEventParams(ulong Hash) { }
        public void MapInputEventToClientEvent_EX1(Enum GroupID, string szInputDefinition, Enum DownEventID, uint DownValue, Enum UpEventID, uint UpValue, bool bMaskable) { }
        public void RemoveInputEvent(Enum GroupID, string szInputDefinition) { }
        public void Dispose() { }
    }
}
