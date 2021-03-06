namespace Logary.PerfTests

open System
open System.Threading
open Expecto
open Logary
open Logary.Configuration
open Logary.Message
open BenchmarkDotNet.Code
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Attributes
open NodaTime

module TestData =
  let helloWorld =
    eventX "Hello world"

  let multiGaugeMessage level =
    Message.event level "Processor.% Idle"
    |> Message.addGauges [
      "Core 1", (Gauge (Fraction (1L, 1000L), Percent))
      "Core 2", (Gauge (Float 0.99, Percent))
      "Core 3", (Gauge (Float 0.473223755, Percent))
    ]
    |> Message.setContext "host" "db-001"
    |> Message.setContext "service" "api-web"

[<AutoOpen>]
module Values =
  let run = Hopac.Hopac.run


  let targets =
    Map [
      "single_nodelay", Targets.BadBoy.create {Targets.BadBoy.empty with delay = Duration.Zero; batch = false } "sink"
      "single_delay", Targets.BadBoy.create {Targets.BadBoy.empty with batch = false }  "sink"
      "batch_nodelay", Targets.BadBoy.create {Targets.BadBoy.empty with delay = Duration.Zero; batch = true }  "sink"
      "batch_delay", Targets.BadBoy.create {Targets.BadBoy.empty with batch = true }  "sink"
    ]

  let baseJob =
    Job.Default
      .WithInvocationCount(800)
      .WithWarmupCount(4)
      .WithLaunchCount(1)
      //.WithIterationTime(TimeInterval.Millisecond * 200)
      .WithGcServer(true)
      .WithGcConcurrent(true)


  let smallInvocationJob =
    Job.Default
      .WithInvocationCount(16)
      .WithWarmupCount(4)
      .WithLaunchCount(1)
      //.WithIterationTime(TimeInterval.Millisecond * 200)
      .WithGcServer(true)
      .WithGcConcurrent(true)

[<Struct>]
type LogaryValue =
  val logger: Logger
  val target: string
  new (target: string) =
    let logary =
      Config.create "Logary.ConsoleApp" "localhost"
      |> Config.target (targets |> Map.find target)
      |> Config.buildAndRun
    { logger = logary.getLogger (PointName [| "PerfTestLogger" |])
      target = target }

type LogaryParam(value: LogaryValue) =
  interface IParam with
    member x.Value = box value
    member x.DisplayText = sprintf "Logary_%s" value.target
    member x.ToSourceCode() = sprintf "new LogaryValue(\"%s\")" value.target

type BP() =
  let toParam (x: IParam) = x

  [<ParamsSource("Configs"); DefaultValue>]
  val mutable logary: LogaryValue

  member x.Configs() =
    [ "single_nodelay";"batch_delay" ]
    |> Seq.map (LogaryValue >> LogaryParam >> toParam)

  [<Benchmark>]
  member x.smallMsg () =
    run (x.logary.logger.warnWithBP TestData.helloWorld)

  [<Benchmark>]
  member x.largeMsg() =
    run (x.logary.logger.warnWithBP TestData.multiGaugeMessage)

type ACK() =
  let toParam (x: IParam) = x

  [<ParamsSource("LogaryConfigs"); DefaultValue>]
  val mutable logary: LogaryValue

  member x.LogaryConfigs() =
    [ "single_nodelay";"batch_delay" ]
    |> Seq.map (LogaryValue >> LogaryParam >> toParam)

  [<Benchmark>]
  member x.wACK() =
    run (x.logary.logger.warnWithAck TestData.multiGaugeMessage)

type Simple() =
  let toParam (x: IParam) = x

  [<ParamsSource("LogaryConfigs"); DefaultValue>]
  val mutable logary: LogaryValue

  member x.LogaryConfigs() =
    [ "single_nodelay";"batch_delay" ]
    |> Seq.map (LogaryValue >> LogaryParam >> toParam)

  [<Benchmark>]
  member x.simp() =
    x.logary.logger.logSimple (TestData.multiGaugeMessage Warn)


module Tests =
  open BenchmarkDotNet.Diagnosers
  open BenchmarkDotNet.Exporters.Csv
  open BenchmarkDotNet.Reports

  // http://adamsitnik.com/the-new-Memory-Diagnoser/
  // https://benchmarkdotnet.org/Advanced/Params.htm
  // https://github.com/logary/logary/pull/323/files
  [<Tests>]
  let benchmarks =
    let config xJ =
      { benchmarkConfig with
          exporters =
            [ new BenchmarkDotNet.Exporters.Csv.CsvExporter(CsvSeparator.Comma)
              new BenchmarkDotNet.Exporters.HtmlExporter()
              new BenchmarkDotNet.Exporters.RPlotExporter()
            ]
          diagnosers =
            [ new MemoryDiagnoser() ]
          jobs = [ xJ ]
      }

    testList "benchmarks" [
      test "backpressure" {
        let cfg = config (Job(Job.Core, baseJob))
        benchmark<BP> cfg box |> ignore
      }

      test "simple" {
        let cfg = config (Job(Job.Core, baseJob))
        benchmark<Simple> cfg box |> ignore
      }

      test "ack" {
        let cfg = config (Job(Job.Core, smallInvocationJob))
        benchmark<ACK> cfg box |> ignore
      }
    ]

module Program =
  [<EntryPoint>]
  let main argv =
    Environment.SetEnvironmentVariable("System.GC.Server", "true")
    Environment.SetEnvironmentVariable("gcServer", "1")
    use cts = new CancellationTokenSource()
    let defaultConfig = { defaultConfig with ``parallel`` = false }
    runTestsInAssemblyWithCancel cts.Token defaultConfig argv
