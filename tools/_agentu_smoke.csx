using DetPS2.Tests;
try {
  SmokeTests.BiosExtendedRomdir_SecrClearSpuLibSdUdnl();
  SmokeTests.BiosUdnl_IopRpImageApplyAndSecrMgPath();
  Console.WriteLine("=== AGENT-U SMOKES PASSED ===");
  return 0;
} catch (Exception ex) {
  Console.Error.WriteLine("FAILED: " + ex);
  return 1;
}
