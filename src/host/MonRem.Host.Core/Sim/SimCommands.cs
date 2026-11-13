using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Sim;

/// <summary>
/// Executa escrita e eventos no sim e sempre devolve um SimAck: nenhuma falha do backend
/// (sim desconectado, timeout, exceção inesperada) derruba a conexão de quem pediu.
/// </summary>
public static class SimCommands
{
    public const int MaxEventArgs = 5;

    public static Task<SimAck> SetAsync(SimRouter router, SimSetVar request, CancellationToken ct)
    {
        var datum = request.ValueCase == SimSetVar.ValueOneofCase.Text ? SimDatum.Of(request.Text) : SimDatum.Of(request.Number);
        return RunAsync(router, request.Ref, SimOperation.Set, (backend, r) => backend.SetAsync(r, datum, ct));
    }

    public static Task<SimAck> FireAsync(SimRouter router, SimFireEvent request, CancellationToken ct)
    {
        if (request.Params.Count > MaxEventArgs)
            return Task.FromResult(Nack(ErrorCode.ErrorInvalidArgument, $"no máximo {MaxEventArgs} parâmetros"));
        var args = request.Params.ToArray();
        return RunAsync(router, request.Ref, SimOperation.Fire, (backend, r) => backend.FireAsync(r, args, ct));
    }

    public static Task<SimAck> ExecuteAsync(SimRouter router, Devices.DeviceAction action, CancellationToken ct) =>
        action.Operation == SimOperation.Set
            ? RunAsync(router, action.Ref, SimOperation.Set, (backend, r) => backend.SetAsync(r, action.Value, ct))
            : RunAsync(router, action.Ref, SimOperation.Fire, (backend, r) => backend.FireAsync(r, action.Args, ct));

    private static async Task<SimAck> RunAsync(SimRouter router, string reference, SimOperation operation, Func<ISimBackend, SimRef, Task> run)
    {
        try
        {
            var parsed = SimRef.Parse(reference);
            await run(router.Resolve(parsed, operation), parsed).ConfigureAwait(false);
            return new SimAck { Ok = true };
        }
        catch (FormatException ex)
        {
            return Nack(ErrorCode.ErrorInvalidArgument, ex.Message);
        }
        catch (SimException ex)
        {
            return Nack(ex.Code, ex.Message);
        }
        catch (TimeoutException)
        {
            return Nack(ErrorCode.ErrorSimNotConnected, "o sim não respondeu a tempo");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Nack(ErrorCode.ErrorInternal, ex.Message);
        }
    }

    private static SimAck Nack(ErrorCode code, string message) => new() { Ok = false, Error = new Error { Code = code, Message = message } };
}
