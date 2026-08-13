# Security and recovery

Please report security issues privately to the NV-UV maintainer before opening
a public issue.

## Recovery

If DCC is interrupted and a cap appears to remain active:

1. open DCC and use **Emergency reset**; or
2. run an elevated terminal command:

   ```powershell
   nvidia-smi --reset-gpu-clocks -i 0
   ```

Replace `0` with the affected GPU index. A driver reload or Windows restart
also clears NVIDIA's temporary locked-clock state.

Never flash a VBIOS to recover a DCC session.
