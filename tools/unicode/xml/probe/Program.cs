using XmlProbe;
if(Environment.GetEnvironmentVariable("GREGSEX_XML_PHASE")=="counters")
    throw new InvalidOperationException("Custom allocation counters retired; BenchmarkDotNet is authoritative");
var fixture=new Fixture();
fixture.VerifyAndWarm();
Console.WriteLine($"prepared warm={Fixture.WarmIterations} utf16={fixture.CharMatrix()} utf8={fixture.ByteMatrix()}; custom counters retired");
