﻿#light

namespace Vim.Modes.Insert
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open System

[<RequireQualifiedAccess>]
type InsertKind =

    | Normal

    /// This Insert is a repeat operation this holds the count and 
    /// whether or not a newline should be inserted after the text
    | Repeat of int * bool * TextChange

    | Block of BlockSpan

/// Data relating to a particular Insert mode session
type InsertSessionData = {

    /// The transaction bracketing the edits of this session
    Transaction : ILinkedUndoTransaction option

    /// The kind of insert we are currently performing
    InsertKind : InsertKind

    /// The most recent TextChange for the insert session
    InsertTextChange : TextChange option

    /// This is the current InsertCommand being built up
    CombinedEditCommand : InsertCommand option

    /// This is the active IWordCompletionSession if one exists
    ActiveWordCompletionSession : IWordCompletionSession option
}

[<RequireQualifiedAccess>]
type RawInsertCommand =
    | InsertCommand of KeyInputSet * InsertCommand * CommandFlags
    | CustomCommand of (unit -> ProcessResult)

type internal InsertMode
    ( 
        _vimBuffer : IVimBuffer, 
        _operations : ICommonOperations,
        _broker : IDisplayWindowBroker, 
        _editorOptions : IEditorOptions,
        _undoRedoOperations : IUndoRedoOperations,
        _textChangeTracker : ITextChangeTracker,
        _insertUtil : IInsertUtil,
        _isReplace : bool,
        _keyboard : IKeyboardDevice,
        _mouse : IMouseDevice,
        _wordUtil : IWordUtil,
        _wordCompletionSessionFactoryService : IWordCompletionSessionFactoryService
    ) as this =

    static let _emptySessionData = {
        InsertKind = InsertKind.Normal
        InsertTextChange = None
        Transaction = None
        CombinedEditCommand = None
        ActiveWordCompletionSession = None
    }

    let _bag = DisposableBag()
    let _textView = _vimBuffer.TextView
    let _textBuffer = _vimBuffer.TextBuffer
    let _globalSettings = _vimBuffer.GlobalSettings
    let _editorOperations = _operations.EditorOperations
    let _commandRanEvent = StandardEvent<CommandRunDataEventArgs>()
    let mutable _commandMap : Map<KeyInput, RawInsertCommand> = Map.empty
    let mutable _sessionData = _emptySessionData

    /// The set of commands supported by insert mode
    static let s_commands : (string * InsertCommand * CommandFlags) list =
        [
            ("<BS>", InsertCommand.Back, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<Del>", InsertCommand.Delete, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<Enter>", InsertCommand.InsertNewLine, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<Left>", InsertCommand.MoveCaret Direction.Left, CommandFlags.Movement)
            ("<Down>", InsertCommand.MoveCaret Direction.Down, CommandFlags.Movement)
            ("<Right>", InsertCommand.MoveCaret Direction.Right, CommandFlags.Movement)
            ("<Tab>", InsertCommand.InsertTab, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<Up>", InsertCommand.MoveCaret Direction.Up, CommandFlags.Movement)
            ("<C-h>", InsertCommand.Back, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<C-i>", InsertCommand.InsertTab, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<C-d>", InsertCommand.ShiftLineLeft, CommandFlags.Repeatable)
            ("<C-m>", InsertCommand.InsertNewLine, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
            ("<C-t>", InsertCommand.ShiftLineRight, CommandFlags.Repeatable)
            ("<C-w>", InsertCommand.DeleteWordBeforeCursor, CommandFlags.Repeatable ||| CommandFlags.InsertEdit)
        ]

    do
        let oldCommands : (string * RawInsertCommand) list = 
            [
                ("<Esc>", RawInsertCommand.CustomCommand this.ProcessEscape)
                ("<Insert>", RawInsertCommand.CustomCommand this.ProcessInsert)
                ("<C-o>", RawInsertCommand.CustomCommand this.ProcessNormalModeOneCommand)
                ("<C-p>", RawInsertCommand.CustomCommand this.ProcessWordCompletionPrevious)
                ("<C-n>", RawInsertCommand.CustomCommand this.ProcessWordCompletionNext)
            ]

        let mappedCommands : (string * RawInsertCommand) list = 
            s_commands
            |> Seq.map (fun (name, command, commandFlags) ->
                let keyInputSet = KeyNotationUtil.StringToKeyInputSet name
                let rawInsertCommand = RawInsertCommand.InsertCommand (keyInputSet, command, commandFlags)
                (name, rawInsertCommand))
            |> List.ofSeq

        let both = Seq.append oldCommands mappedCommands
        _commandMap <-
            oldCommands
            |> Seq.append mappedCommands
            |> Seq.map (fun (str, func) -> (KeyNotationUtil.StringToKeyInput str), func)
            |> Map.ofSeq

        // Caret changes can end a text change operation.
        _textView.Caret.PositionChanged
        |> Observable.subscribe (fun _ -> this.OnCaretPositionChanged() )
        |> _bag.Add

        // Listen for text changes 
        _textChangeTracker.ChangeCompleted
        |> Observable.filter (fun _ -> this.IsActive)
        |> Observable.subscribe (fun args -> this.OnTextChangeCompleted args)
        |> _bag.Add

    member x.ActiveWordCompletionSession = _sessionData.ActiveWordCompletionSession

    member x.CaretPoint = TextViewUtil.GetCaretPoint _textView

    member x.CaretVirtualPoint = TextViewUtil.GetCaretVirtualPoint _textView

    member x.CaretLine = TextViewUtil.GetCaretLine _textView

    member x.CurrentSnapshot = _textView.TextSnapshot

    member x.ModeKind = if _isReplace then ModeKind.Replace else ModeKind.Insert

    /// Is this the currently active mode?
    member x.IsActive = x.ModeKind = _vimBuffer.ModeKind

    /// Cancel the active IWordCompletionSession if there is such a session 
    /// active
    member x.CancelWordCompletionSession () = 
        match _sessionData.ActiveWordCompletionSession with
        | None -> 
            ()
        | Some wordCompletionSession -> 
            if not wordCompletionSession.IsDismissed then
                wordCompletionSession.Dismiss()

            _sessionData <- { _sessionData with ActiveWordCompletionSession = None }

    /// Can Insert mode handle this particular KeyInput value 
    member x.CanProcess keyInput = x.GetRawInsertCommand keyInput |> Option.isSome

    /// Complete the current batched edit command if one exists
    member x.CompleteCombinedEditCommand () = 
        match _sessionData.CombinedEditCommand with
        | None ->
            // Nothing to do
            () 
        | Some command -> 
            _sessionData <- { _sessionData with CombinedEditCommand = None }

            let data = {
                CommandBinding = CommandBinding.InsertBinding (KeyInputSet.Empty, CommandFlags.Repeatable ||| CommandFlags.InsertEdit, command)
                Command = Command.InsertCommand command
                CommandResult = CommandResult.Completed ModeSwitch.NoSwitch }
            let args = CommandRunDataEventArgs(data)
            _commandRanEvent.Trigger x args

    /// Get the RawInsertCommand for the given KeyInput
    member x.GetRawInsertCommand keyInput = 
        match Map.tryFind keyInput _commandMap with
        | Some rawInsertCommand -> Some rawInsertCommand
        | None ->
            match keyInput.RawChar with
            | None -> None
            | Some c ->
                // It's a direct insert if it's a char not mapped to an existing command
                let command = InsertCommand.DirectInsert c
                let commandFlags = CommandFlags.Repeatable ||| CommandFlags.InsertEdit
                let keyInputSet = KeyInputSet.OneKeyInput keyInput
                RawInsertCommand.InsertCommand (keyInputSet, command, commandFlags) |> Some

    /// Get the Span for the word we are trying to complete if there is one
    member x.GetWordCompletionSpan () =
        if SnapshotLineUtil.IsBlank x.CaretLine then
            // Have to special case a bit here.  Blank lines are actually words but we
            // don't want to replace the new line when doing a completion
            None
        elif SnapshotPointUtil.IsBlankOrInsideLineBreak x.CaretPoint && x.CaretPoint.Position > 0 then
            // If we are currently on a blank and the previous point is the end of a word
            // then we are replacing that word
            let previousPoint = SnapshotPointUtil.SubtractOne x.CaretPoint
            _wordUtil.GetFullWordSpan WordKind.NormalWord previousPoint
        else
            // Calculate the word span based on the information before the caret. 
            let point = 
                SnapshotPointUtil.TryGetPreviousPointOnLine x.CaretPoint 1
                |> OptionUtil.getOrDefault x.CaretPoint

            match _wordUtil.GetFullWordSpan WordKind.NormalWord point with
            | None -> None
            | Some span -> SnapshotSpan(span.Start, x.CaretPoint) |> Some


    /// Get the word completions for the given word text in the ITextBuffer
    member x.GetWordCompletions (wordSpan : SnapshotSpan) =

        // Get the sequence of words before the completion word 
        let wordsBefore = 
            let startPoint = SnapshotUtil.GetStartPoint x.CurrentSnapshot
            _wordUtil.GetWords WordKind.NormalWord Path.Forward startPoint
            |> Seq.filter (fun span -> span.End.Position <= wordSpan.Start.Position)

        // Get the sequence of words after the completion word 
        let wordsAfter =

            // The provided SnapshotSpan can be a subset of an entire word.  If so then
            // we want to consider the text to the right of the caret as a full word
            match _wordUtil.GetFullWordSpan WordKind.NormalWord wordSpan.Start with
            | None -> 
                _wordUtil.GetWords WordKind.NormalWord Path.Forward wordSpan.End
            | Some fullWordSpan ->
                if fullWordSpan = wordSpan then
                    _wordUtil.GetWords WordKind.NormalWord Path.Forward wordSpan.End
                else
                    let remaining = SnapshotSpan(wordSpan.End, fullWordSpan.End)
                    let after = _wordUtil.GetWords WordKind.NormalWord Path.Forward fullWordSpan.End
                    Seq.append (Seq.singleton remaining) after

        let filterText = wordSpan.GetText()
        let filterFunc =

            // Is this actually a word we're interest in.  Need to clear out new lines, 
            // comment characters, one character items, etc ... 
            let isWord text =
                if StringUtil.isNullOrEmpty text then 
                    false
                elif text.Length = 1 then
                    // One character items don't get included in the list
                    false
                else
                    TextUtil.IsWordChar WordKind.NormalWord (text.[0])

            if String.IsNullOrEmpty filterText then
                (fun (wordSpan : SnapshotSpan) ->
                    let wordText = wordSpan.GetText()
                    isWord wordText)
            else
                let comparer = if _globalSettings.IgnoreCase then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
                (fun (wordSpan : SnapshotSpan) -> 
                    let wordText = wordSpan.GetText()
                    if wordText.StartsWith(filterText, comparer) && isWord wordText then
                        true
                    else
                        false)

        // Combine the collections
        Seq.append wordsAfter wordsBefore
        |> Seq.filter filterFunc
        |> Seq.map SnapshotSpanUtil.GetText
        |> Seq.distinct
        |> List.ofSeq

    /// Is this KeyInput a raw text insert into the ITextBuffer.  Anything that would be 
    /// processed by adding characters to the ITextBuffer.  This is anything which has an
    /// associated character that is not an insert mode command
    member x.IsDirectInsert (keyInput : KeyInput) = 
        match x.GetRawInsertCommand keyInput with
        | None -> false
        | Some rawInsertCommand ->
            match rawInsertCommand with
            | RawInsertCommand.InsertCommand (_, insertCommand, _) ->
                match insertCommand with
                | InsertCommand.DirectInsert _ -> true
                | _ -> false
            | RawInsertCommand.CustomCommand _ -> false

    /// Apply any edits which must occur after insert mode is completed
    member x.ApplyAfterEdits() = 

        // Flush out any existing text changes so the CombinedEditCommand has the final
        // edit data for the session
        _textChangeTracker.CompleteChange()

        try
            match _sessionData.InsertKind with
            | InsertKind.Normal -> ()
            | InsertKind.Repeat (count, addNewLines, textChange)-> _insertUtil.RepeatEdit textChange addNewLines _isReplace (count - 1)
            | InsertKind.Block blockSpan -> 
                match _sessionData.CombinedEditCommand with
                | None -> ()
                | Some command -> _insertUtil.RepeatBlock command blockSpan

        finally
            // Make sure to close out the transaction
            match _sessionData.Transaction with
            | None -> 
                ()
            | Some transaction -> 
                transaction.Complete()
                _sessionData <- { _sessionData with Transaction = None }

    /// Process the <Insert> command.  This toggles between insert an replace mode
    member x.ProcessInsert () = 

        let mode = if _isReplace then ModeKind.Insert else ModeKind.Replace
        ProcessResult.Handled (ModeSwitch.SwitchMode mode)

    /// Enter normal mode for a single command.
    member x.ProcessNormalModeOneCommand () =

        let switch = ModeSwitch.SwitchModeOneTimeCommand
        ProcessResult.Handled switch

    /// Process the CTRL-N key stroke which calls for the previous word completion
    member x.ProcessWordCompletionNext () = 
        x.StartWordCompletionSession true

    /// Process the CTRL-P key stroke which calls for the previous word completion
    member x.ProcessWordCompletionPrevious () =
        x.StartWordCompletionSession false

    member x.ProcessEscape () =

        let maybeMoveCaretLeft () = 
            // Move the caret one to the left.  Note that this actually counts as a Vim command and is itself
            // a repeatable item.  It works with insert mode by linking with the previous change
            let moveCaretLeft () = 
    
                // TODO: Perhaps we can clean this up a bit.  Do we need to go through all of the ceremony of
                // creating the KeyInputSet?
                let keyInputSet = KeyNotationUtil.StringToKeyInputSet "<Left>"
                let commandFlags = CommandFlags.Repeatable ||| CommandFlags.LinkedWithPreviousCommand
                x.RunInsertCommand (InsertCommand.MoveCaret Direction.Left) keyInputSet commandFlags |> ignore

            // Don't move the caret for block inserts.  It's explicitly positioned 
            match _sessionData.InsertKind with
            | InsertKind.Normal -> moveCaretLeft()
            | InsertKind.Repeat _ -> moveCaretLeft()
            | InsertKind.Block _ -> ()

        this.ApplyAfterEdits()

        if _broker.IsCompletionActive || _broker.IsSignatureHelpActive || _broker.IsQuickInfoActive then
            _broker.DismissDisplayWindows()
            maybeMoveCaretLeft()
            ProcessResult.OfModeKind ModeKind.Normal

        else
            // Need to adjust the caret on exit.  Typically it's just a move left by 1 but if we're
            // in virtual space we just need to get out of it.
            let virtualPoint = TextViewUtil.GetCaretVirtualPoint _textView
            if virtualPoint.IsInVirtualSpace then 
                _operations.MoveCaretToPoint virtualPoint.Position
            else
                maybeMoveCaretLeft()
            ProcessResult.OfModeKind ModeKind.Normal

    /// Start a word completion session in the given direction at the current caret point
    member x.StartWordCompletionSession isForward = 

        // If the caret is currently in virtual space we need to fill in that space with
        // real spaces before starting a completion session.
        if x.CaretVirtualPoint.IsInVirtualSpace then
            let blanks = 
                let blanks = StringUtil.repeatChar x.CaretVirtualPoint.VirtualSpaces ' '
                _operations.NormalizeBlanks blanks

            // Make sure to position the caret to the end of the newly inserted spaces
            let position = x.CaretPoint.Position + blanks.Length
            _textBuffer.Insert(x.CaretPoint.Position, blanks) |> ignore
            TextViewUtil.MoveCaretToPosition _textView position

        // Time to start a completion.  
        let wordSpan = 
            match x.GetWordCompletionSpan() with
            | Some span -> span
            | None -> SnapshotSpan(x.CaretPoint, 0)
        let wordList  = x.GetWordCompletions wordSpan

        // If we have at least one item then begin a word completion session.  Don't do this for an 
        // empty completion list as there is nothing to display.  The lack of anything to display 
        // doesn't make the command an error though
        if not (List.isEmpty wordList) then
            let wordCompletionSession = _wordCompletionSessionFactoryService.CreateWordCompletionSession _textView wordSpan wordList true

            if not wordCompletionSession.IsDismissed then

                // When the completion session is dismissed we want to clean out the session 
                // data 
                wordCompletionSession.Dismissed
                |> Event.add (fun _ -> x.CancelWordCompletionSession())

                _sessionData <- { _sessionData with ActiveWordCompletionSession = Some wordCompletionSession }

        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Run the insert command with the given information
    member x.RunInsertCommand (command : InsertCommand) (keyInputSet : KeyInputSet) commandFlags : ProcessResult =

        // Dismiss the completion when running an explicit insert commend
        x.CancelWordCompletionSession()

        // When running an explicit command then we need to go ahead and complete the previous 
        // extra text change.  It needs to be completed now so that it happens before the 
        // command we are about to run
        _textChangeTracker.CompleteChange()

        let result = 
            try
                // We don't want the edits which are executed as part of the command to be tracked through 
                // an external / extra text change so disable tracking while executing the command
                _textChangeTracker.Enabled <- false
                _insertUtil.RunInsertCommand command
            finally
                _textChangeTracker.Enabled <- true

        x.OnAfterRunInsertCommand command

        // Now we need to decided how the external world sees this edit.  If it links with an
        // existing edit then we save it and send it out as a batch later.
        let isEdit = Util.IsFlagSet commandFlags CommandFlags.InsertEdit
        if isEdit then

            // If it's an edit then combine it with the existing command and batch them 
            // together.  Don't raise the event yet
            let command = 
                match _sessionData.CombinedEditCommand with
                | None -> command
                | Some previousCommand -> InsertCommand.Combined (previousCommand, command)
            _sessionData <- { _sessionData with CombinedEditCommand = Some command }

        else
            // Not an edit command.  If there is an existing edit command then go ahead and flush
            // it out before raising this command
            x.CompleteCombinedEditCommand()

            let data = {
                CommandBinding = CommandBinding.InsertBinding (keyInputSet, commandFlags, command)
                Command = Command.InsertCommand command
                CommandResult = result }
            let args = CommandRunDataEventArgs(data)
            _commandRanEvent.Trigger x args

        ProcessResult.OfCommandResult result

    /// Try and process the KeyInput by considering the current text edit in Insert Mode
    member x.ProcessWithCurrentChange keyInput = 

        // Actually try and process this with the current change 
        let func (text : string) = 
            let data = 
                if text.EndsWith("0") && keyInput = KeyInputUtil.CharWithControlToKeyInput 'd' then
                    let keyInputSet = KeyNotationUtil.StringToKeyInputSet "0<C-d>"
                    Some (InsertCommand.DeleteAllIndent, keyInputSet, "0")
                else
                    None

            match data with
            | None ->
                None
            | Some (command, keyInputSet, text) ->

                // First step is to delete the portion of the current change which matches up with
                // our command.
                if x.CaretPoint.Position >= text.Length then
                    let span = 
                        let startPoint = SnapshotPoint(x.CurrentSnapshot, x.CaretPoint.Position - text.Length)
                        SnapshotSpan(startPoint, text.Length)
                    _textBuffer.Delete(span.Span) |> ignore

                // Now run the command
                x.RunInsertCommand command keyInputSet CommandFlags.Repeatable |> Some

        match _sessionData.InsertTextChange with
        | None ->
            None
        | Some textChange ->
            match textChange.LastChange with
            | TextChange.DeleteLeft _ -> None
            | TextChange.DeleteRight _ -> None
            | TextChange.Insert text -> func text
            | TextChange.Combination _ -> None

    /// Called when we need to process a key stroke and an IWordCompletionSession
    /// is active.
    member x.ProcessWithWordCompletionSession (wordCompletionSession : IWordCompletionSession) keyInput = 
        let handled = 
            if keyInput = KeyNotationUtil.StringToKeyInput("<C-n>") then
                wordCompletionSession.MoveNext() |> Some
            elif keyInput = KeyNotationUtil.StringToKeyInput("<Down>") then
                wordCompletionSession.MoveNext() |> Some
            elif keyInput = KeyNotationUtil.StringToKeyInput("<C-p>") then
                wordCompletionSession.MovePrevious() |> Some
            elif keyInput = KeyNotationUtil.StringToKeyInput("<Up>") then
                wordCompletionSession.MovePrevious() |> Some
            else
                None
        match handled with
        | Some handled -> 
            if handled then 
                ProcessResult.Handled ModeSwitch.NoSwitch 
            else 
                ProcessResult.Error
        | None -> 
            // Any other key should cancel the IWordCompletionSession and we should process
            // the KeyInput as normal
            x.CancelWordCompletionSession()
            x.Process keyInput

    /// Process the KeyInput value
    member x.Process keyInput = 

        match _sessionData.ActiveWordCompletionSession with
        | Some wordCompletionSession -> 
            x.ProcessWithWordCompletionSession wordCompletionSession keyInput
        | None -> 
    
            // Next try and process by examining the current change
            match x.ProcessWithCurrentChange keyInput with
            | Some result ->
                result
            | None ->
                match x.GetRawInsertCommand keyInput with
                | Some rawInsertCommand ->
                    match rawInsertCommand with
                    | RawInsertCommand.CustomCommand func -> func()
                    | RawInsertCommand.InsertCommand (keyInputSet, insertCommand, commandFlags) -> x.RunInsertCommand insertCommand keyInputSet commandFlags
                | None -> 
                    ProcessResult.NotHandled

    /// This is raised when caret changes.  If this is the result of a user click then 
    /// we need to complete the change.
    ///
    /// Need to be careful to not end the edit due to the caret moving as a result of 
    /// normal typing
    ///
    /// TODO: We really need to reconsider how this is used.  If the user has mapped say 
    /// '1' to 'Left' then we will misfire here.  Not a huge concern I think but we need
    /// to find a crisper solution here.
    member x.OnCaretPositionChanged () = 
        if _mouse.IsLeftButtonPressed then 
            _textChangeTracker.CompleteChange()
        elif _vimBuffer.ModeKind = ModeKind.Insert then 
            let keyMove = 
                [ VimKey.Left; VimKey.Right; VimKey.Up; VimKey.Down ]
                |> Seq.map (fun k -> KeyInputUtil.VimKeyToKeyInput k)
                |> Seq.filter (fun k -> _keyboard.IsKeyDown k.Key)
                |> SeqUtil.isNotEmpty
            if keyMove then 
                _textChangeTracker.CompleteChange()

    member x.OnAfterRunInsertCommand command =

        let commandTextChange = 
            match command with 
            | InsertCommand.Back ->  Some (TextChange.DeleteLeft 1)
            | InsertCommand.Combined _ -> None
            | InsertCommand.Delete -> Some (TextChange.DeleteRight 1)
            | InsertCommand.DeleteAllIndent -> None
            | InsertCommand.DeleteWordBeforeCursor -> None
            | InsertCommand.DirectInsert c -> Some (TextChange.Insert (c.ToString()))
            | InsertCommand.ExtraTextChange textChange -> Some textChange
            | InsertCommand.InsertNewLine -> Some (TextChange.Insert (EditUtil.NewLine _editorOptions))
            | InsertCommand.InsertTab -> Some (TextChange.Insert "\t")
            | InsertCommand.MoveCaret _ -> None
            | InsertCommand.ShiftLineLeft -> None
            | InsertCommand.ShiftLineRight -> None

        let insertTextChange = 
            match _sessionData.InsertTextChange, commandTextChange with
            | Some left, Some right -> TextChange.Merge left right |> Some
            | None, Some right -> Some right
            | _ -> None
        _sessionData <- { _sessionData with InsertTextChange = insertTextChange }

        let updateRepeat count addNewLines textChange =

            let insertKind = 
                match commandTextChange with
                | None -> 
                    // Certain actions such as caret movement cause us to abandon the repeat session
                    // and move to a normal insert
                    InsertKind.Normal
                | Some otherTextChange ->
                    let textChange = TextChange.Merge textChange otherTextChange
                    InsertKind.Repeat (count, addNewLines, textChange)

            _sessionData <- { _sessionData with InsertKind = insertKind }

        match _sessionData.InsertKind with
        | InsertKind.Normal -> ()
        | InsertKind.Repeat (count, addNewLines, textChange) -> updateRepeat count addNewLines textChange
        | InsertKind.Block _ -> ()

    /// Raised on the completion of a TextChange event.  This event is not raised immediately
    /// and instead is added to the CombinedEditCommand value for this session which will be 
    /// raised as a command at a later time
    member x.OnTextChangeCompleted (args : TextChangeEventArgs) =

        let textChange = args.TextChange
        let command = 
            let textChangeCommand = InsertCommand.ExtraTextChange textChange
            x.OnAfterRunInsertCommand textChangeCommand
            match _sessionData.CombinedEditCommand with
            | None -> textChangeCommand
            | Some command -> InsertCommand.Combined (command, textChangeCommand)

        _sessionData <- { _sessionData with CombinedEditCommand = Some command } 

    /// Called when the IVimBuffer is closed.  We need to unsubscribe from several events
    /// when this happens to prevent the ITextBuffer / ITextView from being kept alive
    member x.OnClose () =
        _bag.DisposeAll()

    /// Entering an insert or replace mode.  Setup the InsertSessionData based on the 
    /// ModeArgument value. 
    member x.OnEnter arg =

        // When starting insert mode we want to track the edits to the IVimBuffer as a 
        // text change
        _textChangeTracker.Enabled <- true

        // On enter we need to check the 'count' and possibly set up a transaction to 
        // lump edits and their repeats together
        let transaction, insertKind =
            match arg with
            | ModeArgument.InsertBlock blockSpan ->
                let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                Some transaction, InsertKind.Block blockSpan
            | ModeArgument.InsertWithCount count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, InsertKind.Repeat (count, false, TextChange.Insert StringUtil.empty)
                else
                    None, InsertKind.Normal
            | ModeArgument.InsertWithCountAndNewLine count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, InsertKind.Repeat (count, true, TextChange.Insert StringUtil.empty)
                else
                    None, InsertKind.Normal
            | ModeArgument.InsertWithTransaction transaction ->
                Some transaction, InsertKind.Normal
            | _ -> 
                if _isReplace then
                    // Replace mode occurs under a transaction even if we are not repeating
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, InsertKind.Normal
                else
                    None, InsertKind.Normal

        // If the LastCommand coming into insert / replace mode is not setup for linking 
        // with the next change then clear it out now.  This is needed to implement functions
        // like 'dw' followed by insert, <Esc> and immediately by '.'.  It should simply 
        // move the caret left
        match _vimBuffer.VimData.LastCommand with
        | None ->
            ()
        | Some lastCommand ->
            if not (Util.IsFlagSet lastCommand.CommandFlags CommandFlags.LinkedWithNextCommand) then
                _vimBuffer.VimData.LastCommand <- None

        _sessionData <- {
            Transaction = transaction
            InsertKind = insertKind
            InsertTextChange = None
            CombinedEditCommand = None
            ActiveWordCompletionSession = None
        }

        // If this is replace mode then go ahead and setup overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, true)

    /// Called when leaving insert mode.  Here we will do any remaining cleanup on the
    /// InsertSessionData.  It's possible to get here with active session data if there
    /// is an exception during the processing of the transaction.
    ///
    /// Or more sinister.  A simple API call to OnLeave could force us to leave while 
    /// a transaction was open
    member x.OnLeave () =

        // When leaving insert mode we complete the current change
        _textChangeTracker.CompleteChange()
        _textChangeTracker.Enabled <- false

        // Possibly raise the edit command.  This will have already happened if <Esc> was used
        // to exit insert mode.  This case takes care of being asked to exit programmatically 
        x.CompleteCombinedEditCommand()

        // Dismiss any active ICompletionSession 
        x.CancelWordCompletionSession()

        try
            match _sessionData.Transaction with
            | None -> ()
            | Some transaction -> transaction.Complete()
        finally
            _sessionData <- _emptySessionData

        // If this is replace mode then go ahead and undo overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false)

    interface IInsertMode with 
        member x.ActiveWordCompletionSession = x.ActiveWordCompletionSession
        member x.VimTextBuffer = _vimBuffer.VimTextBuffer
        member x.CommandNames =  _commandMap |> Seq.map (fun p -> p.Key) |> Seq.map OneKeyInput
        member x.ModeKind = x.ModeKind
        member x.CanProcess keyInput = x.CanProcess keyInput
        member x.IsDirectInsert keyInput = x.IsDirectInsert keyInput
        member x.Process keyInput = x.Process keyInput
        member x.OnEnter arg = x.OnEnter arg
        member x.OnLeave () = x.OnLeave ()
        member x.OnClose() = x.OnClose ()

        [<CLIEvent>]
        member x.CommandRan = _commandRanEvent.Publish

