using System.Runtime.CompilerServices;

// The test project places cars at exact world points (internal setters on
// Car.X/Y/Speed/Heading) to unit-test the safety systems in isolation.
[assembly: InternalsVisibleTo("DrivingGame.Sim.Tests")]
