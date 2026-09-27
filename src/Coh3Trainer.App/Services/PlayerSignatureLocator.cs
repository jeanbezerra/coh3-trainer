using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Coh3Trainer.Interop;
using Microsoft.Win32.SafeHandles;

namespace Coh3Trainer.Services;

/// <summary>
/// Resolves the local-player resource object using the two probes documented
/// by the public CoH3 Final Stand table. Complete instruction sequences are
/// checked before a hook is installed or recovered.
/// </summary>
internal sealed class PlayerSignatureLocator : IDisposable
{
    private const int PlayerHookOffset = 9;
    private const int PlayerHookLength = 15;
    private const int QueueHookLength = 16;
    private const int AllocationSize = 0x1000;
    private const int PlayerCodeOffset = 0x10;
    private const int QueueCodeOffset = 0x100;

    private static readonly byte?[] PlayerProbePattern = ParsePattern(
        "48 83 EC 68 48 85 C9 74 0F ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? " +
        "48 8D 15 ?? ?? ?? ?? 48 8D 4C 24 38");
    private static readonly byte?[] QueueProbePattern = ParsePattern(
        "?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? " +
        "EB 4E 48 8B D7 48 8B CE E8 ?? ?? ?? ?? 43 8B 04 2C 8B 8E B8 00 00 00");

    private static readonly byte[] ExpectedPlayerBytes =
    {
        0x80, 0xB9, 0x82, 0x06, 0x00, 0x00, 0x00,
        0x0F, 0x95, 0xC0,
        0x48, 0x83, 0xC4, 0x68,
        0xC3
    };
    private static readonly byte[] ExpectedQueueBytes =
    {
        0x80, 0xBF, 0x59, 0x05, 0x00, 0x00, 0x00,
        0x74, 0x09,
        0xC7, 0x46, 0x30, 0x00, 0x00, 0x00, 0x00
    };
    private readonly Process _process;
    private readonly SafeProcessHandle _processHandle;
    private readonly ProcessModule _module;
    private nint _storageAddress;
    private nint _playerHookAddress;
    private nint _playerCodeAddress;
    private nint _queueHookAddress;
    private nint _queueCodeAddress;
    private bool _installed;

    public PlayerSignatureLocator(Process process, SafeProcessHandle processHandle, ProcessModule module)
    {
        _process = process;
        _processHandle = processHandle;
        _module = module;
    }

    public TrainerResult Install()
    {
        if (_installed)
        {
            return TrainerResult.Ok("Resolvedor automático já está ativo.");
        }

        var recoveredExistingHook = false;
        try
        {
            _playerHookAddress = FindUnique(PlayerProbePattern, "do jogador") + PlayerHookOffset;
            var playerBytes = ReadExact(_playerHookAddress, PlayerHookLength);
            if (TryAdoptPlayerHook(playerBytes))
            {
                recoveredExistingHook = true;
            }
            else
            {
                if (!playerBytes.SequenceEqual(ExpectedPlayerBytes))
                {
                    throw new InvalidOperationException(
                        "As instruções do jogador contêm uma alteração desconhecida.");
                }

                _storageAddress = NativeMethods.VirtualAllocEx(
                    _processHandle,
                    nint.Zero,
                    AllocationSize,
                    NativeMethods.MemCommit | NativeMethods.MemReserve,
                    NativeMethods.PageExecuteReadWrite);
                if (_storageAddress == nint.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao reservar a área de captura.");
                }

                _playerCodeAddress = _storageAddress + PlayerCodeOffset;
                WriteExact(_playerCodeAddress, BuildPlayerCaptureStub(_storageAddress));
                PatchInstructions(
                    _playerHookAddress,
                    BuildAbsoluteJump(_playerCodeAddress, PlayerHookLength));
            }

            _queueHookAddress = FindUnique(QueueProbePattern, "da fila de produção");
            _queueCodeAddress = _storageAddress + QueueCodeOffset;
            var queueBytes = ReadExact(_queueHookAddress, QueueHookLength);
            if (TryAdoptQueueHook(queueBytes))
            {
                recoveredExistingHook = true;
            }
            else
            {
                if (!queueBytes.SequenceEqual(ExpectedQueueBytes))
                {
                    throw new InvalidOperationException(
                        "As instruções da fila contêm uma alteração desconhecida.");
                }

                WriteExact(
                    _queueCodeAddress,
                    BuildQueueCaptureStub(_storageAddress, _queueHookAddress));
                PatchInstructions(
                    _queueHookAddress,
                    BuildAbsoluteJump(_queueCodeAddress, QueueHookLength));
            }

            _installed = true;
            return TrainerResult.Ok(recoveredExistingHook
                ? "Identificação automática recuperada de uma conexão anterior."
                : "Identificação automática instalada; aguardando o jogador local.");
        }
        catch (Exception ex)
        {
            TryRestoreHooks();
            return TrainerResult.Fail($"Falha ao ativar a identificação automática: {ex.Message}");
        }
    }

    public bool TryGetPlayerAddress(out nint playerAddress)
    {
        playerAddress = nint.Zero;
        if (!_installed || _storageAddress == nint.Zero)
        {
            return false;
        }

        try
        {
            var pointerBytes = ReadExact(_storageAddress, sizeof(long));
            playerAddress = (nint)BitConverter.ToInt64(pointerBytes);
            return playerAddress != nint.Zero;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private nint FindUnique(byte?[] pattern, string description)
    {
        var matches = FindPatternMatches(pattern, 2);
        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"A assinatura automática {description} não foi encontrada nesta versão."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"A assinatura automática {description} não é única.")
        };
    }

    private bool TryAdoptPlayerHook(byte[] currentBytes)
    {
        if (!TryReadAbsoluteJump(currentBytes, PlayerHookLength, out var codeAddress))
        {
            return false;
        }

        var storageAddress = codeAddress - PlayerCodeOffset;
        if (storageAddress.ToInt64() < 0x10000)
        {
            return false;
        }

        try
        {
            var expectedStub = BuildPlayerCaptureStub(storageAddress);
            if (!ReadExact(codeAddress, expectedStub.Length).SequenceEqual(expectedStub))
            {
                return false;
            }

            _storageAddress = storageAddress;
            _playerCodeAddress = codeAddress;
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private bool TryAdoptQueueHook(byte[] currentBytes)
    {
        if (!TryReadAbsoluteJump(currentBytes, QueueHookLength, out var codeAddress) ||
            codeAddress != _queueCodeAddress)
        {
            return false;
        }

        try
        {
            var expectedStub = BuildQueueCaptureStub(_storageAddress, _queueHookAddress);
            return ReadExact(codeAddress, expectedStub.Length).SequenceEqual(expectedStub);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private IReadOnlyList<nint> FindPatternMatches(byte?[] pattern, int limit)
    {
        const int chunkSize = 1024 * 1024;
        var matches = new List<nint>();
        var moduleStart = _module.BaseAddress.ToInt64();
        var moduleEnd = checked(moduleStart + _module.ModuleMemorySize);
        var address = moduleStart;

        while (address < moduleEnd && matches.Count < limit)
        {
            var queried = NativeMethods.VirtualQueryEx(
                _processHandle,
                (nint)address,
                out var region,
                (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
            if (queried == 0)
            {
                break;
            }

            var regionStart = Math.Max(address, region.BaseAddress.ToInt64());
            var rawRegionEnd = checked(region.BaseAddress.ToInt64() + (long)region.RegionSize);
            var regionEnd = Math.Min(moduleEnd, rawRegionEnd);
            if (region.State == NativeMethods.MemCommit &&
                (region.Protect & (NativeMethods.PageGuard | NativeMethods.PageNoAccess)) == 0)
            {
                var step = chunkSize - pattern.Length + 1;
                for (var chunkAddress = regionStart;
                     chunkAddress < regionEnd && matches.Count < limit;
                     chunkAddress += step)
                {
                    var requested = checked((int)Math.Min(chunkSize, regionEnd - chunkAddress));
                    var buffer = new byte[requested];
                    var readSucceeded = NativeMethods.ReadProcessMemory(
                        _processHandle,
                        (nint)chunkAddress,
                        buffer,
                        (nuint)requested,
                        out var bytesRead);
                    if ((!readSucceeded && bytesRead == 0) || bytesRead < (nuint)pattern.Length)
                    {
                        continue;
                    }

                    var available = checked((int)bytesRead);
                    for (var index = 0; index <= available - pattern.Length; index++)
                    {
                        if (!MatchesAt(buffer, index, pattern))
                        {
                            continue;
                        }

                        var matchAddress = (nint)(chunkAddress + index);
                        if (!matches.Contains(matchAddress))
                        {
                            matches.Add(matchAddress);
                        }

                        if (matches.Count >= limit)
                        {
                            break;
                        }
                    }
                }
            }

            address = regionEnd > address ? regionEnd : address + 0x1000;
        }

        return matches;
    }

    private void PatchInstructions(nint address, byte[] bytes)
    {
        var suspendedThreads = SuspendGameThreads();
        try
        {
            if (!NativeMethods.VirtualProtectEx(
                    _processHandle,
                    address,
                    (nuint)bytes.Length,
                    NativeMethods.PageExecuteReadWrite,
                    out var previousProtection))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao liberar a página de código.");
            }

            try
            {
                WriteExact(address, bytes);
                if (!NativeMethods.FlushInstructionCache(_processHandle, address, (nuint)bytes.Length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao atualizar o cache de instruções.");
                }
            }
            finally
            {
                NativeMethods.VirtualProtectEx(
                    _processHandle,
                    address,
                    (nuint)bytes.Length,
                    previousProtection,
                    out _);
            }
        }
        finally
        {
            ResumeGameThreads(suspendedThreads);
        }
    }

    private List<SafeWaitHandle> SuspendGameThreads()
    {
        var suspended = new List<SafeWaitHandle>();
        try
        {
            _process.Refresh();
            foreach (ProcessThread thread in _process.Threads)
            {
                var handle = NativeMethods.OpenThread(NativeMethods.ThreadSuspendResume, false, (uint)thread.Id);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    continue;
                }

                if (NativeMethods.SuspendThread(handle) == uint.MaxValue)
                {
                    handle.Dispose();
                    continue;
                }

                suspended.Add(handle);
            }

            if (suspended.Count == 0)
            {
                throw new InvalidOperationException("Nenhuma thread do jogo pôde ser suspensa com segurança.");
            }

            return suspended;
        }
        catch
        {
            ResumeGameThreads(suspended);
            throw;
        }
    }

    private static void ResumeGameThreads(IEnumerable<SafeWaitHandle> handles)
    {
        foreach (var handle in handles.Reverse())
        {
            NativeMethods.ResumeThread(handle);
            handle.Dispose();
        }
    }

    private byte[] ReadExact(nint address, int count)
    {
        var buffer = new byte[count];
        if (!NativeMethods.ReadProcessMemory(_processHandle, address, buffer, (nuint)count, out var bytesRead) ||
            bytesRead != (nuint)count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Falha ao ler 0x{address:X}.");
        }

        return buffer;
    }

    private void WriteExact(nint address, byte[] bytes)
    {
        if (!NativeMethods.WriteProcessMemory(_processHandle, address, bytes, (nuint)bytes.Length, out var bytesWritten) ||
            bytesWritten != (nuint)bytes.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Falha ao escrever 0x{address:X}.");
        }
    }

    private static byte[] BuildPlayerCaptureStub(nint storageAddress)
    {
        var bytes = new List<byte>
        {
            0x80, 0xB9, 0x82, 0x06, 0x00, 0x00, 0x00, // cmp byte ptr [rcx+682], 0
            0x75, 0x0D,                               // jne skip_capture
            0x48, 0xB8                                // mov rax, storageAddress
        };
        bytes.AddRange(BitConverter.GetBytes(storageAddress.ToInt64()));
        bytes.AddRange(new byte[]
        {
            0x48, 0x89, 0x08,                         // mov [rax], rcx
            0x80, 0xB9, 0x82, 0x06, 0x00, 0x00, 0x00, // original cmp
            0x0F, 0x95, 0xC0,                         // setne al
            0x48, 0x83, 0xC4, 0x68,                   // add rsp, 68
            0xC3                                      // ret
        });
        return bytes.ToArray();
    }

    private static byte[] BuildQueueCaptureStub(nint storageAddress, nint queueHookAddress)
    {
        var bytes = new List<byte>
        {
            0x80, 0xBF, 0x82, 0x06, 0x00, 0x00, 0x00, // cmp byte ptr [rdi+682], 0
            0x75, 0x0F,                               // jne skip_capture
            0x50,                                     // push rax
            0x48, 0xB8                                // mov rax, storageAddress
        };
        bytes.AddRange(BitConverter.GetBytes(storageAddress.ToInt64()));
        bytes.AddRange(new byte[]
        {
            0x48, 0x89, 0x38,                         // mov [rax], rdi
            0x58,                                     // pop rax
            0x80, 0xBF, 0x59, 0x05, 0x00, 0x00, 0x00, // original cmp byte ptr [rdi+559], 0
            0x75, 0x0E                                // jne non_zero
        });
        bytes.AddRange(BuildAbsoluteJump(queueHookAddress + 18, 14));
        bytes.AddRange(new byte[]
        {
            0xC7, 0x46, 0x30, 0x00, 0x00, 0x00, 0x00  // original mov dword ptr [rsi+30], 0
        });
        bytes.AddRange(BuildAbsoluteJump(queueHookAddress + 16, 14));
        return bytes.ToArray();
    }

    private static byte[] BuildAbsoluteJump(nint destination, int length)
    {
        if (length < 14)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var patch = Enumerable.Repeat((byte)0x90, length).ToArray();
        patch[0] = 0xFF;
        patch[1] = 0x25;
        patch[2] = 0;
        patch[3] = 0;
        patch[4] = 0;
        patch[5] = 0;
        BitConverter.GetBytes(destination.ToInt64()).CopyTo(patch, 6);
        return patch;
    }

    private static bool TryReadAbsoluteJump(byte[] bytes, int length, out nint destination)
    {
        destination = nint.Zero;
        if (bytes.Length != length ||
            bytes[0] != 0xFF || bytes[1] != 0x25 ||
            bytes[2] != 0 || bytes[3] != 0 || bytes[4] != 0 || bytes[5] != 0 ||
            bytes.Skip(14).Any(value => value != 0x90))
        {
            return false;
        }

        destination = (nint)BitConverter.ToInt64(bytes, 6);
        return destination.ToInt64() >= 0x10000;
    }

    private static bool MatchesAt(byte[] buffer, int start, byte?[] pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index].HasValue && buffer[start + index] != pattern[index]!.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static byte?[] ParsePattern(string pattern) => pattern
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value == "??" ? (byte?)null : Convert.ToByte(value, 16))
        .ToArray();

    private bool TryRestoreHooks()
    {
        var queueRestored = TryRestoreHook(
            _queueHookAddress,
            ExpectedQueueBytes,
            _queueCodeAddress,
            QueueHookLength);
        var playerRestored = TryRestoreHook(
            _playerHookAddress,
            ExpectedPlayerBytes,
            _playerCodeAddress,
            PlayerHookLength);

        if (queueRestored && playerRestored)
        {
            _installed = false;
        }

        return queueRestored && playerRestored;
    }

    private bool TryRestoreHook(nint hookAddress, byte[] originalBytes, nint codeAddress, int hookLength)
    {
        if (hookAddress == nint.Zero)
        {
            return true;
        }

        try
        {
            var currentBytes = ReadExact(hookAddress, hookLength);
            if (currentBytes.SequenceEqual(originalBytes))
            {
                return true;
            }

            if (codeAddress == nint.Zero ||
                !currentBytes.SequenceEqual(BuildAbsoluteJump(codeAddress, hookLength)))
            {
                return false;
            }

            PatchInstructions(hookAddress, originalBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if ((_installed || _storageAddress != nint.Zero) && !TryRestoreHooks())
        {
            // Se o jogo já encerrou, o sistema operacional recuperará a alocação.
            return;
        }

        // O stub pode ainda estar em execução em outra thread. Mantê-lo alocado até
        // o encerramento do jogo elimina a corrida entre restaurar o hook e liberá-lo.
        _storageAddress = nint.Zero;
        _playerHookAddress = nint.Zero;
        _playerCodeAddress = nint.Zero;
        _queueHookAddress = nint.Zero;
        _queueCodeAddress = nint.Zero;
    }
}
