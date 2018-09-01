﻿using System;
using System.Threading;
using System.Threading.Tasks;
using BrickController2.HardwareServices;
using BrickController2.Helpers;

namespace BrickController2.DeviceManagement
{
    internal class InfraredDeviceManager : IInfraredDeviceManager
    {
        private const int IR_FREQUENCY = 38000;

        private const int IR_MARK_US = 158;
        private const int IR_START_GAP_US = 1026;
        private const int IR_ONE_GAP_US = 553;
        private const int IR_ZERO_GAP_US = 263;
        private const int IR_STOP_GAP_US = 2000;

        private const int MAX_SEND_ATTEMPTS = 8;

        private readonly IInfraredService _infraredService;
        private readonly AsyncLock _asyncLock = new AsyncLock();

        private readonly int[,] _outputValues = new int[4, 2];
        private readonly int[] _sendAttemptsLeft = new int[4];
        private readonly int[] _irData = new int[18 * 2];

        private int _connectedDevicesCount = 0;
        private Task _irTask;
        private CancellationTokenSource _irTaskCancelationTokenSource;

        public InfraredDeviceManager(IInfraredService infraredService)
        {
            _infraredService = infraredService;
        }

        public async Task ScanAsync(Func<DeviceType, string, string, Task> deviceFoundCallback, CancellationToken token)
        {
            if (_infraredService.IsInfraredSupported && _infraredService.IsCarrierFrequencySupported(IR_FREQUENCY))
            {
                await deviceFoundCallback(DeviceType.Infrared, "PF Infra 1", "0");
                await deviceFoundCallback(DeviceType.Infrared, "PF Infra 2", "1");
                await deviceFoundCallback(DeviceType.Infrared, "PF Infra 3", "2");
                await deviceFoundCallback(DeviceType.Infrared, "PF Infra 4", "3");
            }
        }

        public async Task<DeviceConnectionResult> ConnectDevice(InfraredDevice device)
        {
            using (await _asyncLock.LockAsync())
            {
                if (_connectedDevicesCount >= 4)
                {
                    return DeviceConnectionResult.Error;
                }

                _connectedDevicesCount++;
                if (_connectedDevicesCount == 1)
                {
                    await StartIrThread();
                }

                return DeviceConnectionResult.Ok;
            }
        }

        public async Task DisconnectDevice(InfraredDevice device)
        {
            using (await _asyncLock.LockAsync())
            {
                if (_connectedDevicesCount == 0)
                {
                    return;
                }

                _connectedDevicesCount--;
                if (_connectedDevicesCount == 0)
                {
                    await StopIrThreadAsync();
                }
            }
        }

        public async Task SetOutput(InfraredDevice device, int channel, int value)
        {
            using (await _asyncLock.LockAsync())
            {
                if (int.TryParse(device.Address, out int address))
                {
                    if (_outputValues[address, channel] == value)
                    {
                        return;
                    }

                    _outputValues[address, channel] = value;
                    _sendAttemptsLeft[address] = MAX_SEND_ATTEMPTS;
                }
            }
        }

        private void ResetOutputs()
        {
            for (int i = 0; i < 4; i++)
            {
                _outputValues[i, 0] = 0;
                _outputValues[i, 1] = 0;
                _sendAttemptsLeft[i] = MAX_SEND_ATTEMPTS;
            }
        }

        private async Task StartIrThread()
        {
            await StopIrThreadAsync();
            ResetOutputs();

            _irTaskCancelationTokenSource = new CancellationTokenSource();
            _irTask = Task.Run(async () =>
            {
                try
                {
                    while (_irTaskCancelationTokenSource.Token.IsCancellationRequested)
                    {
                        await SendIrData(_irTaskCancelationTokenSource.Token);
                        await Task.Delay(5);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private async Task StopIrThreadAsync()
        {
            if (_irTask == null)
            {
                return;
            }

            _irTaskCancelationTokenSource.Cancel();
            await _irTask;

            _irTask = null;
            _irTaskCancelationTokenSource.Dispose();
            _irTaskCancelationTokenSource = null;
        }

        private async Task SendIrData(CancellationToken token)
        {
            for (int address = 0; address < 4; address++)
            {
                token.ThrowIfCancellationRequested();

                if (_sendAttemptsLeft[address] > 0)
                {
                    int value0 = _outputValues[address, 0];
                    int value1 = _outputValues[address, 1];

                    FillIrBuffer(value0, value1, address);

                    await _infraredService.SendPacketAsync(IR_FREQUENCY, _irData);
                    await Task.Delay(2, token);

                    if (value0 != 0 || value1 != 0)
                    {
                        _sendAttemptsLeft[address] = MAX_SEND_ATTEMPTS;
                    }
                    else
                    {
                        _sendAttemptsLeft[address] -= 1;
                    }
                }
            }
        }

        private void FillIrBuffer(int value0, int value1, int address)
        {
            int nibble1 = 0x4 | address;
            int nibble2 = CalculateOutputNibble(value0);
            int nibble3 = CalculateOutputNibble(value1);
            int nibbleLrc = 0xf ^ nibble1 ^ nibble2 ^ nibble3;

            int index = 0;
            index = AppendStart(index);
            index = AppendNibble(index, nibble1);
            index = AppendNibble(index, nibble2);
            index = AppendNibble(index, nibble3);
            index = AppendNibble(index, nibbleLrc);
            AppendStop(index);
        }

        private int CalculateOutputNibble(int value)
        {
            if (value < 0)
            {
                return (8 - (Math.Abs(value) >> 5)) | 8;
            }
            else
            {
                return value >> 5;
            }
        }

        private int AppendStart(int index)
        {
            _irData[index++] = IR_MARK_US;
            _irData[index++] = IR_START_GAP_US;
            return index;
        }

        private int AppendStop(int index)
        {
            _irData[index++] = IR_MARK_US;
            _irData[index++] = IR_STOP_GAP_US;
            return index;
        }

        private int AppendBit(int index, int bit)
        {
            _irData[index++] = IR_MARK_US;
            _irData[index++] = (bit != 0) ? IR_ONE_GAP_US : IR_ZERO_GAP_US;
            return index;
        }

        private int AppendNibble(int index, int nibble)
        {
            index = AppendBit(index, nibble & 8);
            index = AppendBit(index, nibble & 4);
            index = AppendBit(index, nibble & 2);
            index = AppendBit(index, nibble & 1);
            return index;
        }
    }
}