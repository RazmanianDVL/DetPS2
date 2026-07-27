# Netplay-Certified Titles (synthetic fixtures only — no commercial title certified)

Det mode + rollback soaks for **synthetic** fixtures.  
Commercial certification requires user dumps and a longer 2P session.

| id | Status |
|----|--------|
| homebrew-gs-demo | Certified (synthetic + soak) |
| input-replay-determinism | Certified (synthetic) |
| homebrew-rollback-2p | Certified (synthetic 2P sim) |
| iso-boot-homebrew | Certified (synthetic list) |
| stub-bios-harness | Certified (synthetic list) |

## Protocol

- Rollback window default 8  
- Frame advantage default 1  
- Transports: TCP LAN, UDP prototype, in-memory  
- CLI: `dotnet run --project src/DetPS2.Core -c Release -- netplay-cert 600`  
- Det mode only on the wire  

See [ROLLBACK.md](ROLLBACK.md) and [COMPLETENESS.md](../COMPLETENESS.md).
