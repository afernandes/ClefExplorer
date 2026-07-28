using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Garante uma única janela do aplicativo por usuário e encaminha para ela os caminhos
    /// abertos depois.
    ///
    /// <para>Antes, selecionar cinco arquivos <c>.clef</c> no Explorer e dar Enter abria
    /// <b>cinco janelas</b>, cada uma com um arquivo. Agora a primeira instância assume o
    /// mutex e escuta um named pipe; as seguintes apenas mandam seus caminhos e encerram.</para>
    ///
    /// <para>Os nomes usam o escopo <c>Local\</c> (padrão), então cada sessão de usuário tem
    /// a sua própria instância — o comportamento correto num servidor de terminal.</para>
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = "ClefExplorer.SingleInstance.v1";
        private const string PipeName = "ClefExplorer.Instance.v1";
        private const int ConnectTimeoutMs = 2000;

        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _cts = new();

        private SingleInstance(Mutex mutex) => _mutex = mutex;

        /// <summary>
        /// Tenta se tornar a instância principal. Devolve <c>false</c> quando já existe
        /// outra — nesse caso use <see cref="SendToExistingInstance"/>.
        /// </summary>
        public static bool TryAcquire(out SingleInstance? instance)
        {
            instance = null;
            try
            {
                var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                instance = new SingleInstance(mutex);
                return true;
            }
            catch (Exception ex)
            {
                // Sem o mutex seguimos como instância normal: perder o "instância única"
                // é bem melhor do que não abrir o aplicativo.
                AppLog.Warning("Falha ao verificar instância única; seguindo como instância independente", ex);
                return true;
            }
        }

        /// <summary>Envia os caminhos para a instância que já está rodando.</summary>
        public static bool SendToExistingInstance(string[] paths)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(ConnectTimeoutMs);

                var payload = Encoding.UTF8.GetBytes(string.Join("\n", paths));
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível falar com a instância existente", ex);
                return false;
            }
        }

        /// <summary>
        /// Passa a escutar caminhos enviados por outras instâncias. O callback é invocado
        /// numa thread de background — quem recebe é responsável por voltar à thread de UI.
        /// </summary>
        public void StartListening(Action<string[]> onPathsReceived)
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(_cts.Token);

                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var content = await reader.ReadToEndAsync(_cts.Token);

                        var paths = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (paths.Length > 0)
                        {
                            onPathsReceived(paths);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return; // encerrando
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Falha ao receber caminhos de outra instância", ex);
                        // Espera um instante para um erro persistente não virar laço quente.
                        try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { return; }
                    }
                }
            });
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao liberar a instância única", ex);
            }
        }
    }
}
