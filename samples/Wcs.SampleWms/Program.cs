// The sample WMS: dotnet run --project samples/Wcs.SampleWms
// It listens on http://localhost:5142 (the address the WCS host calls by default) and answers the
// WCS -> WMS contract from an in-memory warehouse. See README.md.
Wcs.SampleWms.SampleWmsApp.Create(args).Run();
