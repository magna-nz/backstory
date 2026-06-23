using Backstory.Eval;

var report = await new EvalRunner().RunAsync();

Console.WriteLine("Backstory benchmark");
Console.WriteLine("===================");
Console.WriteLine($"Ingestion coverage : {report.IngestionCoverage:P1}  ({report.EventsEmitted}/{report.EventsExpected} events)");
Console.WriteLine($"Retrieval Recall@5 : {report.RecallAt5:P1}  ({report.Hits}/{report.Questions} questions)");
