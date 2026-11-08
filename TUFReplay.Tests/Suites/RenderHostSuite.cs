using Newtonsoft.Json.Linq;
using TUFReplay.Replay.Export;
using TUFReplay.Replay.Rendering;
using static TestFixture;

// TUFReplay replays renders as OrbitRender's host: it reads its own recording bundle back and
// tells OrbitRender only where the level is and which media to compose.
internal static class RenderHostSuite
{
  internal static void RunAll(string root)
  {
    ReadsTheBundleItWrote(root);
    PreservesNativeInputIdentity(root);
    TellsOrbitRenderAbsoluteMediaPaths(root);
    RefusesMediaOutsideTheBundle(root);
    Console.WriteLine("TUFReplay render host tests passed.");
  }

  private static void ReadsTheBundleItWrote(string root)
  {
    RecordingBundle bundle = RecordingBundle.Load(WriteBundle(root, "read", media: null));
    Assert(
      bundle.Inputs.Length == 2 && bundle.Inputs[0].Key == "A" && bundle.Inputs[0].Down,
      "Recorded keys were not read."
    );
    Assert(bundle.Hits.Length == 1 && bundle.Hits[0].Margin == "Perfect", "Recorded hits were not read.");
    Assert(bundle.Manifest.Replay.TerminalTimeUs == 2_000_000, "The end of the recording was not read.");
    Console.WriteLine("PASS the render host reads the recording bundle TUFReplay exports");
  }

  private static void PreservesNativeInputIdentity(string root)
  {
    string path = WriteBundle(root, "native", media: null);
    string directory = Path.GetDirectoryName(path);
    using var csv = new StringWriter();
    RenderBundleCsv.WriteInputs(
      csv,
      new[]
      {
        new TUFReplay.Replay.Models.RecordedInput(0, 0x15, TUFReplay.Replay.Models.RecordInputFlags.Down),
        new TUFReplay.Replay.Models.RecordedInput(1, 0xA5, TUFReplay.Replay.Models.RecordInputFlags.Down),
        new TUFReplay.Replay.Models.RecordedInput(2, 0x15, 0),
        new TUFReplay.Replay.Models.RecordedInput(3, 0xA5, 0),
      },
      "windows",
      3,
      includeNativeKeys: true
    );
    File.WriteAllText(Path.Combine(directory, "inputs.native.csv"), csv.ToString());
    JObject manifest = JObject.Parse(File.ReadAllText(path));
    manifest["nativeInputsFile"] = "inputs.native.csv";
    File.WriteAllText(path, manifest.ToString());
    RecordingBundle bundle = RecordingBundle.Load(path);
    Assert(bundle.Inputs.Length == 4, "The native input companion file was ignored.");
    Assert(
      bundle.Inputs[0].WindowsNativeKey == 0x15 && bundle.Inputs[1].WindowsNativeKey == 0xA5,
      "Hangul and right Alt lost their distinct native identities."
    );
    Assert(
      !bundle.Inputs[2].Down && bundle.Inputs[2].WindowsNativeKey == 0x15,
      "The Hangul release lost its native identity."
    );
    Assert(
      !RecordingBundle.Load(WriteBundle(root, "legacy-native", media: null)).Inputs[0].WindowsNativeKey.HasValue,
      "A legacy bundle invented a native key."
    );
    using var mac = new StringWriter();
    RenderBundleCsv.WriteInputs(
      mac,
      new[] { new TUFReplay.Replay.Models.RecordedInput(0, 0x15, 0) },
      "macos",
      0,
      includeNativeKeys: true
    );
    Assert(
      !RecordingCsvReader.ReadInputs(new StringReader(mac.ToString()))[0].WindowsNativeKey.HasValue,
      "A macOS native key was interpreted as a Windows key."
    );
    AssertThrows<RecordingFormatException>(
      () =>
        RecordingCsvReader.ReadInputs(
          new StringReader(RecordingCsvReader.InputsHeader + ",windowsNativeKey\n0,RightAlt,1,0,256\n")
        ),
      "An invalid Windows native key was accepted."
    );
    manifest["nativeInputsFile"] = "../outside.csv";
    File.WriteAllText(path, manifest.ToString());
    AssertThrows<RecordingFormatException>(
      () => RecordingBundle.Load(path),
      "The native input file escaped the bundle directory."
    );
  }

  private static void TellsOrbitRenderAbsoluteMediaPaths(string root)
  {
    var media = new JObject
    {
      ["webcam"] = new JObject { ["path"] = "media/camera.mp4", ["offsetMs"] = 12 },
      ["microphone"] = new JObject { ["path"] = "media/microphone.wav", ["volume"] = 1.5 },
    };
    string manifest = WriteBundle(root, "media", media);
    RecordingBundle bundle = RecordingBundle.Load(manifest);
    JObject request = JObject.Parse(
      RenderJobCoordinator.HostedRequestJson(bundle, new OrbitRenderOptions { OutputDirectory = "/videos" })
    );
    string directory = Path.GetDirectoryName(Path.GetFullPath(manifest));
    Assert((string)request["levelPath"] == "/levels/test.adofai", "The level path did not reach OrbitRender.");
    Assert(
      (int)request["startTile"] == 3 && (double)request["gameInputOffsetMs"] == 15,
      "Replay timing did not reach OrbitRender."
    );
    Assert((long)request["wonTimeUs"] == 1_500_000, "The clear time did not reach OrbitRender.");
    Assert(
      (string)request["media"]["webcam"]["path"] == Path.Combine(directory, "media", "camera.mp4")
        && (int)request["media"]["webcam"]["offsetMs"] == 12,
      "The camera did not reach OrbitRender with an absolute path and its settings."
    );
    Assert(
      (string)request["media"]["microphone"]["path"] == Path.Combine(directory, "media", "microphone.wav"),
      "The microphone did not reach OrbitRender with an absolute path."
    );
    Assert((string)request["options"]["outputDirectory"] == "/videos", "The render options did not reach OrbitRender.");
    Assert(
      request["inputsFile"] == null && request["recordingId"] == null,
      "OrbitRender was sent recording data it no longer reads."
    );
    Console.WriteLine("PASS OrbitRender gets the level, timing and absolute media paths, not the recording");
  }

  private static void RefusesMediaOutsideTheBundle(string root)
  {
    var media = new JObject { ["webcam"] = new JObject { ["path"] = "../outside.mp4" } };
    RecordingBundle bundle = RecordingBundle.Load(WriteBundle(root, "escape", media));
    AssertThrows<RecordingFormatException>(
      () => RenderJobCoordinator.HostedRequestJson(bundle, new OrbitRenderOptions()),
      "A media path outside the bundle was sent to OrbitRender."
    );
    Console.WriteLine("PASS media paths outside the bundle are refused");
  }

  private static string WriteBundle(string root, string name, JObject media)
  {
    string directory = Path.Combine(root, "render-host-" + name);
    Directory.CreateDirectory(directory);
    File.WriteAllText(
      Path.Combine(directory, "inputs.csv"),
      RecordingCsvReader.InputsHeader + "\n-1000,A,1,0\n500000,A,0,1\n"
    );
    File.WriteAllText(
      Path.Combine(directory, "hits.csv"),
      RecordingCsvReader.HitsHeader + "\n400000,1,0.5,0,0,0,0,1.25,3.14,0,0,0,Perfect\n"
    );
    var manifest = new JObject
    {
      ["schemaVersion"] = 1,
      ["recordingId"] = "run-" + name,
      ["level"] = new JObject { ["path"] = "/levels/test.adofai" },
      ["replay"] = new JObject
      {
        ["gameplayStartSongPosition"] = 1.0,
        ["effectivePitch"] = 1.0,
        ["gameInputOffsetMs"] = 15,
        ["noFailMode"] = false,
        ["judgmentSystem"] = "ModernClassic",
        ["startTile"] = 3,
        ["result"] = "cleared",
        ["wonTimeUs"] = 1_500_000,
        ["terminalTimeUs"] = 2_000_000,
      },
      ["inputsFile"] = "inputs.csv",
      ["hitsFile"] = "hits.csv",
    };
    if (media != null)
      manifest["media"] = media;
    string path = Path.Combine(directory, "manifest.json");
    File.WriteAllText(path, manifest.ToString());
    return path;
  }
}
