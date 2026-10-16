using GregsEx.Tests.Flavors.EcmaScript.Unicode;
UnicodeChecks.Run();
Console.WriteLine("Unicode16/Emoji16 complete inventory checks passed; native=" + !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
