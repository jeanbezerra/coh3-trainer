using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Coh3Trainer.Interop;
using Coh3Trainer.Localization;
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
    private const int AllocationSize = 0x2000;
    private const int PlayerCodeOffset = 0x10;
    private const int QueueCodeOffset = 0x100;
    private const int ActionCodeOffset = 0x200;
    private const int ActionRequestOffset = 0x1000;
    private const int ActionResultOffset = 0x1004;
    private const int MaximumPlayerSquads = 256;

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
    private readonly ITextLocalizer _localizer;
    private PlayerSquadActionLayout? _squadActionLayout;
    private nint _storageAddress;
    private nint _playerHookAddress;
    private nint _playerCodeAddress;
    private nint _queueHookAddress;
    private nint _queueCodeAddress;
    private nint _actionCodeAddress;
    private bool _installed;

    public PlayerSignatureLocator(
        Process process,
        SafeProcessHandle processHandle,
        ProcessModule module,
        PlayerSquadActionLayout? squadActionLayout,
        ITextLocalizer? localizer = null)
    {
        _process = process;
        _processHandle = processHandle;
        _module = module;
        _squadActionLayout = squadActionLayout;
        _localizer = localizer ?? LocalizationService.Current;
    }

    public bool SupportsPlayerSquadActions => _installed && _squadActionLayout is not null;

    public TrainerResult Install()
    {
        if (_installed)
        {
            return TrainerResult.Ok(_localizer.Get("Signature.AlreadyActive"));
        }

        var recoveredExistingHook = false;
        try
        {
            if (_squadActionLayout is not null && !ValidateSquadActionLayout(_squadActionLayout))
            {
                _squadActionLayout = null;
            }

            _playerHookAddress = FindUnique(
                PlayerProbePattern,
                _localizer.Get("Signature.PlayerDescription")) + PlayerHookOffset;
            var playerBytes = ReadExact(_playerHookAddress, PlayerHookLength);
            if (TryAdoptPlayerHook(playerBytes))
            {
                recoveredExistingHook = true;
            }
            else
            {
                if (!playerBytes.SequenceEqual(ExpectedPlayerBytes))
                {
                    throw new InvalidOperationException(_localizer.Get("Signature.PlayerInstructionsChanged"));
                }

                _storageAddress = NativeMethods.VirtualAllocEx(
                    _processHandle,
                    nint.Zero,
                    AllocationSize,
                    NativeMethods.MemCommit | NativeMethods.MemReserve,
                    NativeMethods.PageExecuteReadWrite);
                if (_storageAddress == nint.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        _localizer.Get("Signature.CaptureAllocationFailed"));
                }

                _playerCodeAddress = _storageAddress + PlayerCodeOffset;
                InstallSquadActionDispatcher();
                WriteExact(
                    _playerCodeAddress,
                    BuildPlayerCaptureStub(_storageAddress, _actionCodeAddress));
                PatchInstructions(
                    _playerHookAddress,
                    BuildAbsoluteJump(_playerCodeAddress, PlayerHookLength));
            }

            if (recoveredExistingHook)
            {
                InstallOrAdoptSquadActionDispatcher();
            }

            _queueHookAddress = FindUnique(
                QueueProbePattern,
                _localizer.Get("Signature.QueueDescription"));
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
                    throw new InvalidOperationException(_localizer.Get("Signature.QueueInstructionsChanged"));
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
                ? _localizer.Get("Signature.Recovered")
                : _localizer.Get("Signature.Installed"));
        }
        catch (Exception ex)
        {
            TryRestoreHooks();
            return TrainerResult.Fail(_localizer.Get("Signature.ActivationFailed", ex.Message));
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

    public async Task<(bool Completed, int AffectedCount)> ExecutePlayerSquadActionAsync(
        PlayerSquadAction action,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPlayerSquadActions || _storageAddress == nint.Zero)
        {
            return (false, 0);
        }

        var requestAddress = _storageAddress + ActionRequestOffset;
        var resultAddress = _storageAddress + ActionResultOffset;
        if (BitConverter.ToInt32(ReadExact(requestAddress, sizeof(int))) != 0)
        {
            return (false, 0);
        }

        WriteExact(resultAddress, BitConverter.GetBytes(-1));
        WriteExact(requestAddress, BitConverter.GetBytes((int)action));

        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(25, cancellationToken);
            var request = BitConverter.ToInt32(ReadExact(requestAddress, sizeof(int)));
            if (request != 0)
            {
                continue;
            }

            var affected = BitConverter.ToInt32(ReadExact(resultAddress, sizeof(int)));
            return (affected >= 0, Math.Max(affected, 0));
        }

        var pendingRequest = BitConverter.ToInt32(ReadExact(requestAddress, sizeof(int)));
        if (pendingRequest == (int)action)
        {
            WriteExact(requestAddress, BitConverter.GetBytes(0));
        }

        return (false, 0);
    }

    private nint FindUnique(byte?[] pattern, string description)
    {
        var matches = FindPatternMatches(pattern, 2);
        return matches.Count switch
        {
            0 => throw new InvalidOperationException(_localizer.Get("Signature.NotFound", description)),
            1 => matches[0],
            _ => throw new InvalidOperationException(_localizer.Get("Signature.NotUnique", description))
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
            var actionCodeAddress = _squadActionLayout is null
                ? nint.Zero
                : storageAddress + ActionCodeOffset;
            var expectedStub = BuildPlayerCaptureStub(storageAddress, actionCodeAddress);
            if (!ReadExact(codeAddress, expectedStub.Length).SequenceEqual(expectedStub))
            {
                return false;
            }

            _storageAddress = storageAddress;
            _playerCodeAddress = codeAddress;
            _actionCodeAddress = actionCodeAddress;
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private bool ValidateSquadActionLayout(PlayerSquadActionLayout layout)
    {
        var moduleBase = _module.BaseAddress;
        return MatchesPrefix(moduleBase + (nint)layout.GetPlayerSquadsRva,
                   "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 54 41 55 41 56 41 57") &&
               MatchesPrefix(moduleBase + (nint)layout.IncreaseVeterancyRankRva,
                   "48 83 EC 28 44 8B D2 4C 8B C9 48 85 C9") &&
               MatchesPrefix(moduleBase + (nint)layout.SetHealthRva,
                   "48 89 5C 24 18 48 89 6C 24 20 56 57 41 54 41 56 41 57") &&
               MatchesPrefix(moduleBase + (nint)layout.AdjustAbilityCooldownRva,
                   "48 85 C9 74 47 53 48 83 EC 20 8B DA");
    }

    private bool MatchesPrefix(nint address, string expectedHex)
    {
        var expected = expectedHex
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Convert.ToByte(value, 16))
            .ToArray();
        try
        {
            return ReadExact(address, expected.Length).SequenceEqual(expected);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void InstallSquadActionDispatcher()
    {
        if (_squadActionLayout is null)
        {
            _actionCodeAddress = nint.Zero;
            return;
        }

        _actionCodeAddress = _storageAddress + ActionCodeOffset;
        WriteExact(
            _actionCodeAddress,
            BuildSquadActionDispatcher(_storageAddress, _module.BaseAddress, _squadActionLayout));
        InitializeSquadActionStorage();
    }

    private void InstallOrAdoptSquadActionDispatcher()
    {
        if (_squadActionLayout is null)
        {
            _actionCodeAddress = nint.Zero;
            return;
        }

        _actionCodeAddress = _storageAddress + ActionCodeOffset;
        var expected = BuildSquadActionDispatcher(
            _storageAddress,
            _module.BaseAddress,
            _squadActionLayout);
        if (!ReadExact(_actionCodeAddress, expected.Length).SequenceEqual(expected))
        {
            throw new InvalidOperationException(_localizer.Get("Signature.UnitActionInstructionsChanged"));
        }

        InitializeSquadActionStorage();
    }

    private void InitializeSquadActionStorage()
    {
        WriteExact(_storageAddress + ActionResultOffset, BitConverter.GetBytes(0));
        WriteExact(_storageAddress + ActionRequestOffset, BitConverter.GetBytes(0));
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
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    _localizer.Get("Signature.CodePageFailed"));
            }

            try
            {
                WriteExact(address, bytes);
                if (!NativeMethods.FlushInstructionCache(_processHandle, address, (nuint)bytes.Length))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        _localizer.Get("Signature.InstructionCacheFailed"));
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
                throw new InvalidOperationException(_localizer.Get("Signature.NoThreads"));
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
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                _localizer.Get("Signature.ReadFailed", address));
        }

        return buffer;
    }

    private void WriteExact(nint address, byte[] bytes)
    {
        if (!NativeMethods.WriteProcessMemory(_processHandle, address, bytes, (nuint)bytes.Length, out var bytesWritten) ||
            bytesWritten != (nuint)bytes.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                _localizer.Get("Signature.WriteFailed", address));
        }
    }

    private static byte[] BuildPlayerCaptureStub(nint storageAddress, nint actionCodeAddress)
    {
        var bytes = new List<byte>
        {
            0x80, 0xB9, 0x82, 0x06, 0x00, 0x00, 0x00, // cmp byte ptr [rcx+682], 0
            0x75, actionCodeAddress == nint.Zero ? (byte)0x0D : (byte)0x23,
            0x48, 0xB8                                // mov rax, storageAddress
        };
        bytes.AddRange(BitConverter.GetBytes(storageAddress.ToInt64()));
        bytes.AddRange(new byte[] { 0x48, 0x89, 0x08 }); // mov [rax], rcx
        if (actionCodeAddress != nint.Zero)
        {
            bytes.AddRange(new byte[]
            {
                0x51,                                     // push rcx
                0x48, 0x83, 0xEC, 0x28,                   // sub rsp, 28h
                0x48, 0xB8                                // mov rax, actionCodeAddress
            });
            bytes.AddRange(BitConverter.GetBytes(actionCodeAddress.ToInt64()));
            bytes.AddRange(new byte[]
            {
                0xFF, 0xD0,                               // call rax
                0x48, 0x83, 0xC4, 0x28,                   // add rsp, 28h
                0x59                                      // pop rcx
            });
        }

        bytes.AddRange(new byte[]
        {
            0x80, 0xB9, 0x82, 0x06, 0x00, 0x00, 0x00, // original cmp
            0x0F, 0x95, 0xC0,                         // setne al
            0x48, 0x83, 0xC4, 0x68,                   // add rsp, 68
            0xC3                                      // ret
        });
        return bytes.ToArray();
    }

    private static byte[] BuildSquadActionDispatcher(
        nint storageAddress,
        nint moduleBase,
        PlayerSquadActionLayout layout)
    {
        var code = new X64CodeBuilder();

        code.Emit(0x53, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57);
        code.Emit(0x48, 0x83, 0xEC, 0x28);
        code.Emit(0x48, 0x89, 0xCD);                    // mov rbp,rcx (local player)
        code.MovRbxImmediate(storageAddress);
        code.Emit(0x8B, 0x83).Int32(ActionRequestOffset); // mov eax,[rbx+request]
        code.Emit(0x85, 0xC0);                           // test eax,eax
        code.Jump32(0x0F, 0x8E, "finish");              // jle finish
        code.Emit(0x83, 0xF8, 0x03);                    // cmp eax,3
        code.Jump32(0x0F, 0x87, "invalid");             // ja invalid
        code.Emit(0x41, 0x89, 0xC4);                    // mov r12d,eax
        code.Emit(0xBA).Int32(-1);                       // mov edx,-1
        code.Emit(0xF0, 0x0F, 0xB1, 0x93).Int32(ActionRequestOffset); // lock cmpxchg [request],edx
        code.Jump32(0x0F, 0x85, "finish");              // another game thread claimed it
        code.Emit(0xC7, 0x83).Int32(ActionResultOffset).Int32(0);

        code.Emit(0x48, 0x89, 0xE9);                    // mov rcx,rbp
        code.MovRaxImmediate(moduleBase + (nint)layout.GetPlayerSquadsRva);
        code.Emit(0xFF, 0xD0);
        code.Emit(0x48, 0x85, 0xC0);                    // test rax,rax
        code.Jump32(0x0F, 0x84, "invalid");             // je invalid
        code.Emit(0x4C, 0x8B, 0x70, 0x08);              // mov r14,[rax+8]
        code.Emit(0x4C, 0x8B, 0x78, 0x10);              // mov r15,[rax+10h]
        code.Emit(0x4D, 0x39, 0xFE);                    // cmp r14,r15
        code.Jump32(0x0F, 0x87, "invalid");             // ja invalid
        code.Emit(0x4C, 0x89, 0xF8);                    // mov rax,r15
        code.Emit(0x4C, 0x29, 0xF0);                    // sub rax,r14
        code.Emit(0xA8, 0x07);                          // test al,7
        code.Jump32(0x0F, 0x85, "invalid");             // jne invalid
        code.Emit(0x48, 0x3D).Int32(MaximumPlayerSquads * sizeof(long));
        code.Jump32(0x0F, 0x87, "invalid");             // ja invalid
        code.Emit(0x45, 0x31, 0xED);                    // xor r13d,r13d

        code.Label("loop");
        code.Emit(0x4D, 0x39, 0xFE);
        code.Jump32(0x0F, 0x83, "complete");            // jae complete
        code.Emit(0x49, 0x8B, 0x0E);                    // mov rcx,[r14]
        code.Emit(0x49, 0x83, 0xC6, 0x08);              // add r14,8
        code.Emit(0x48, 0x85, 0xC9);
        code.Jump32(0x0F, 0x84, "loop");                // je loop
        code.Emit(0x41, 0x83, 0xFC, 0x01);
        code.Jump32(0x0F, 0x85, "check_heal");
        code.Emit(0xBA).Int32(1);                        // mov edx,1
        code.MovRaxImmediate(moduleBase + (nint)layout.IncreaseVeterancyRankRva);
        code.Emit(0xFF, 0xD0);
        code.Jump32(0xE9, "count");

        code.Label("check_heal");
        code.Emit(0x41, 0x83, 0xFC, 0x02);
        code.Jump32(0x0F, 0x85, "cooldown");
        code.Emit(0xB8).Int32(0x3F800000);               // mov eax,1.0f
        code.Emit(0x66, 0x0F, 0x6E, 0xC8);              // movd xmm1,eax
        code.MovRaxImmediate(moduleBase + (nint)layout.SetHealthRva);
        code.Emit(0xFF, 0xD0);
        code.Jump32(0xE9, "count");

        code.Label("cooldown");
        code.Emit(0xBA).Int32(-86_400_000);              // force every positive remaining duration to zero
        code.MovRaxImmediate(moduleBase + (nint)layout.AdjustAbilityCooldownRva);
        code.Emit(0xFF, 0xD0);

        code.Label("count");
        code.Emit(0x41, 0xFF, 0xC5);                    // inc r13d
        code.Jump32(0xE9, "loop");

        code.Label("complete");
        code.Emit(0x44, 0x89, 0xAB).Int32(ActionResultOffset);
        code.Emit(0xC7, 0x83).Int32(ActionRequestOffset).Int32(0);
        code.Jump32(0xE9, "finish");

        code.Label("invalid");
        code.Emit(0xC7, 0x83).Int32(ActionResultOffset).Int32(-2);
        code.Emit(0xC7, 0x83).Int32(ActionRequestOffset).Int32(0);

        code.Label("finish");
        code.Emit(0x48, 0x83, 0xC4, 0x28);
        code.Emit(0x41, 0x5F, 0x41, 0x5E, 0x41, 0x5D, 0x41, 0x5C, 0x5F, 0x5E, 0x5D, 0x5B, 0xC3);
        return code.Build();
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
            // If the game has already exited, the operating system will reclaim the allocation.
            return;
        }

        // The stub may still be executing on another thread. Keeping it allocated until
        // the game exits avoids a race between restoring the hook and releasing its code.
        _storageAddress = nint.Zero;
        _playerHookAddress = nint.Zero;
        _playerCodeAddress = nint.Zero;
        _queueHookAddress = nint.Zero;
        _queueCodeAddress = nint.Zero;
        _actionCodeAddress = nint.Zero;
    }

    private sealed class X64CodeBuilder
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int DisplacementOffset, string Label)> _fixups = new();

        public X64CodeBuilder Emit(params byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public X64CodeBuilder Int32(int value)
        {
            _bytes.AddRange(BitConverter.GetBytes(value));
            return this;
        }

        public void MovRaxImmediate(nint value)
        {
            Emit(0x48, 0xB8);
            _bytes.AddRange(BitConverter.GetBytes(value.ToInt64()));
        }

        public void MovRbxImmediate(nint value)
        {
            Emit(0x48, 0xBB);
            _bytes.AddRange(BitConverter.GetBytes(value.ToInt64()));
        }

        public void MovRdxImmediate(nint value)
        {
            Emit(0x48, 0xBA);
            _bytes.AddRange(BitConverter.GetBytes(value.ToInt64()));
        }

        public void Label(string name) => _labels.Add(name, _bytes.Count);

        public void Jump32(byte opcode, string label)
        {
            Emit(opcode);
            AddFixup(label);
        }

        public void Jump32(byte opcode1, byte opcode2, string label)
        {
            Emit(opcode1, opcode2);
            AddFixup(label);
        }

        public byte[] Build()
        {
            foreach (var (offset, label) in _fixups)
            {
                if (!_labels.TryGetValue(label, out var target))
                {
                    throw new InvalidOperationException($"Unknown machine-code label: {label}");
                }

                var displacement = BitConverter.GetBytes(target - (offset + sizeof(int)));
                for (var index = 0; index < displacement.Length; index++)
                {
                    _bytes[offset + index] = displacement[index];
                }
            }

            return _bytes.ToArray();
        }

        private void AddFixup(string label)
        {
            var offset = _bytes.Count;
            _bytes.AddRange(new byte[sizeof(int)]);
            _fixups.Add((offset, label));
        }
    }
}
