namespace Logary

open System
open System.Globalization
open System.Text
open System.IO
open Logary

/// A thing that efficiently writes a message to a TextWriter.
type MessageWriter =
  abstract write : TextWriter -> Message -> unit

[<AutoOpen>]
module MessageWriterEx =
  type MessageWriter with
    [<Obsolete "Try to write directly to a System.IO.TextWriter instead">]
    member x.format (m : Message) =
      use sw = StringWriter()
      x.write sw m
      sw.ToString()


/// simple message writer use messagetemplates
/// json writer should use from other project that use fspickler.json
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module MessageWriter =
  open Microsoft.FSharp.Reflection
  open Logary.Formatting.Literate
  open MessageTemplates
  open MessageTemplates.Formatting
  open MessageTemplates.Formatting.Literate
  open Logary.Internals
  

    /// Returns the case name of the object with union type 'ty.
  let private caseNameOf (x:'a) =
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

  /// Format a timestamp in nanoseconds since epoch into a ISO8601 string
  let formatTimestamp (timestamp : EpochNanoSeconds) =
    Instant.ofEpoch(timestamp).ToDateTimeOffset().ToString("o", CultureInfo.InvariantCulture)

  let internal defaultDestr = Destructure.generatePropValue Global.Destructure.destructureFac Global.Destructure.getProjection

  /// maxDepth can be avoided if cycle reference are handled properly
  let expanded nl ending : MessageWriter =
    { new MessageWriter with
        member x.write tw m =
          let writeState = { provider = tw.FormatProvider; idManager = RefIdManager ()}
          let level = string (caseNameOf m.level).[0]
          let time = formatTimestamp m.timestamp
          let body = tokeniseTemplateWithGauges tw.FormatProvider defaultDestr m |> collectAllToString
          let name = m.name.ToString()
          let context = tokeniseContext writeState nl defaultDestr m |> collectAllToString
          sprintf "%s %s: %s [%s]%s%s" level time body name context ending
          |> tw.Write
    }

  /// Verbatim simply outputs the message and no other information
  /// and doesn't append a newline to the string.
  let verbatim =
    { new MessageWriter with
        member x.write tw m =
          tokeniseTemplateWithGauges tw.FormatProvider defaultDestr m
          |> Seq.map fst |> Seq.iter tw.Write
    }

  /// VerbatimNewline simply outputs the omessage and no other information
  /// and does append a newline to the string.
  let verbatimNewLine =
    { new MessageWriter with
        member x.write tw m =
          verbatim.write tw m
          tw.WriteLine()
    }

  /// <see cref="MessageWriter.LevelDatetimePathMessageNewLine" />
  let levelDatetimeMessagePath =
    expanded Environment.NewLine ""

  /// LevelDatetimePathMessageNl outputs the most information of the Message
  /// in text format, starting with the level as a single character,
  /// then the ISO8601 format of a DateTime (with +00:00 to show UTC time),
  /// then the path in square brackets: [Path.Here], the message and a newline.
  /// Exceptions are called ToString() on and prints each line of the stack trace
  /// newline separated.
  let levelDatetimeMessagePathNewLine =
    expanded Environment.NewLine Environment.NewLine

    
  let contextWriter =
    { new MessageWriter with
        member x.write tw m =
          let writeState = { provider = tw.FormatProvider; idManager = RefIdManager ()}
          tokeniseContext writeState Environment.NewLine defaultDestr m
          |> Seq.map fst |> Seq.iter tw.Write
    }