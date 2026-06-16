// ┌──────────────────────────────────────────────────────────────────────────┐
// │  TesterDimseAlias.cs                                                     │
// │                                                                          │
// │  Alias de tipos del DicomPacsTester para que DimseService.cs compile     │
// │  referenciando los nombres del Tester sin colisión con los del Core.     │
// │                                                                          │
// │  En la práctica, los archivos DimseTestService.cs y CMoveService.cs del  │
// │  Tester se copian aquí LITERALMENTE (en la misma carpeta Dimse/) y se    │
// │  ajusta únicamente el namespace a DicomMigrator.Infrastructure.          │
// └──────────────────────────────────────────────────────────────────────────┘

// Alias para que DimseService.cs no repita los tipos del Tester
global using TesterDimseTestService = DicomMigrator.Infrastructure.Services.Dimse.DimseTestService;
global using TesterDimseConfig      = DicomMigrator.Infrastructure.Services.Dimse.TesterDimseConfiguration;
global using TesterCFindQuery       = DicomMigrator.Infrastructure.Services.Dimse.TesterCFindQueryInternal;
global using TesterCMoveRequest     = DicomMigrator.Infrastructure.Services.Dimse.TesterCMoveRequestInternal;
