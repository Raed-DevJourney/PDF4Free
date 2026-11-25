using PDF4Free.Tests;

// "dump-form <file.pdf> <result.json>" inspects a saved form with the engine alone; see FormDump.
if (args is ["dump-form", var pdf, var json]) return FormDump.Run(pdf, json);

// Otherwise: a plain console harness. Each check prints PASS/FAIL and writes artefacts to the "out" folder.
var outDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);
var runner = new TestRunner(outDir, args);
runner.RunAll();
return runner.Failed == 0 ? 0 : 1;
