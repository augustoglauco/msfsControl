using System;
namespace WASimCommander.CLI.Enums
{
    public enum HR { OK = 0, FAIL = -1 }
    public enum CalcResultType : byte { None, Double, Integer, String, Formatted }
    public enum UpdatePeriod : byte { Never, Once, Tick, Millisecond }
}
namespace WASimCommander.CLI.Structs
{
    using WASimCommander.CLI.Enums;
    public class DataRequest
    {
        public uint requestId;
        public DataRequest(uint requestId, CalcResultType resultType, string calculatorCode, UpdatePeriod period, uint interval, float deltaEpsilon) { this.requestId = requestId; }
    }
    public class DataRequestRecord : DataRequest
    {
        public DataRequestRecord() : base(0, CalcResultType.Double, "", UpdatePeriod.Tick, 0, 0) { }
        public static implicit operator double(DataRequestRecord dr) => 0;
    }
}
namespace WASimCommander.CLI.Client
{
    using WASimCommander.CLI.Enums;
    using WASimCommander.CLI.Structs;
    public delegate void DataDelegate(DataRequestRecord r);
    public class WASimClient : IDisposable
    {
        public WASimClient(uint clientId) { }
        public event DataDelegate OnDataReceived;
        public HR connectSimulator(uint? timeout = null) => HR.OK;
        public HR connectServer(uint? timeout = null) => HR.OK;
        public bool isConnected() => true;
        public HR executeCalculatorCode(string code) => HR.OK;
        public HR saveDataRequestAsync(DataRequest request) => HR.OK;
        public HR removeDataRequest(uint requestId) => HR.OK;
        public void Dispose() { }
    }
}
