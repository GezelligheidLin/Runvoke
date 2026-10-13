//! A bounded, single-line terminal buffer for pipe output (not a full terminal).
//! Like Windows Terminal, parsing, cursor movement and text replacement are
//! separate: BS/CR move the cursor; only writes and EL erase existing text.
use unicode_width::UnicodeWidthChar;
use vte::{Params, Parser, Perform};

const MAX_COLUMNS: usize = super::LOG_MESSAGE_LIMIT;
const MAX_TEXT_BYTES: usize = MAX_COLUMNS * 4;
const TRUNCATION_MARKER: &str = "… [单条日志过长，已截断]";

pub(crate) fn is_webpack_progress(message: &str) -> bool {
    // Preserve the pre-existing newline-based webpack compatibility rule in
    // both frontend output and MCP history. Other percentages stay ordinary.
    let lower = message.to_ascii_lowercase();
    let Some((_, rest)) = lower.split_once("[webpack.progress]") else {
        return false;
    };
    if !rest.starts_with(char::is_whitespace) {
        return false;
    }
    let rest = rest.trim_start();
    let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
    if !(1..=3).contains(&digits) {
        return false;
    }
    let Some(suffix) = rest[digits..].strip_prefix('%') else {
        return false;
    };
    suffix.is_empty() || suffix.starts_with(char::is_whitespace)
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) struct LineUpdate {
    pub message: String,
    pub mode: &'static str,
}

#[derive(Default)]
pub(crate) struct TerminalLogParser {
    parser: Parser,
    line: LineBuffer,
}

impl TerminalLogParser {
    pub fn feed(&mut self, bytes: &[u8]) -> Vec<LineUpdate> {
        self.parser.advance(&mut self.line, bytes);
        self.line.publish_progress();
        std::mem::take(&mut self.line.updates)
    }

    pub fn finish(&mut self) -> Vec<LineUpdate> {
        // Cancel any unfinished escape sequence and let vte replace a trailing
        // incomplete UTF-8 character. CAN itself has no effect on visible text.
        self.parser.advance(&mut self.line, &[0x18]);
        if !self.line.cells.is_empty() || self.line.truncated || self.line.published {
            self.line.complete();
        }
        std::mem::take(&mut self.line.updates)
    }
}

#[derive(Clone, Default)]
enum Cell {
    #[default]
    Blank,
    Glyph {
        text: String,
        width: usize,
    },
    Continuation,
}

#[derive(Default)]
struct LineBuffer {
    cells: Vec<Cell>,
    cursor: usize,
    text_bytes: usize,
    truncated: bool,
    pending_cr: bool,
    rewritten: bool,
    published: bool,
    dirty: bool,
    last_snapshot: Option<String>,
    updates: Vec<LineUpdate>,
}

impl LineBuffer {
    fn message(&self) -> String {
        let mut message = String::new();
        for cell in &self.cells {
            match cell {
                Cell::Blank => message.push(' '),
                Cell::Glyph { text, .. } => message.push_str(text),
                Cell::Continuation => {}
            }
        }
        // Erased terminal padding isn't log content. Preserve ordinary spaces.
        if self.rewritten {
            message.truncate(message.trim_end_matches(' ').len());
        }
        if self.truncated {
            message.push_str(TRUNCATION_MARKER);
        }
        message
    }

    fn publish_progress(&mut self) {
        if !(self.rewritten || self.pending_cr) || !self.dirty {
            return;
        }
        self.dirty = false;
        let message = self.message();
        if (message.is_empty() && !self.published) || self.last_snapshot.as_ref() == Some(&message)
        {
            return;
        }
        self.last_snapshot = Some(message.clone());
        self.updates.push(LineUpdate {
            message,
            mode: "progress",
        });
        self.published = true;
    }

    fn complete(&mut self) {
        self.updates.push(LineUpdate {
            message: self.message(),
            mode: if self.published || self.rewritten {
                "finish"
            } else {
                "append"
            },
        });
        self.cells.clear();
        self.cursor = 0;
        self.text_bytes = 0;
        self.truncated = false;
        self.pending_cr = false;
        self.rewritten = false;
        self.published = false;
        self.dirty = false;
        self.last_snapshot = None;
    }

    fn move_cursor(&mut self, column: usize) {
        self.cursor = column.min(MAX_COLUMNS);
        self.rewritten = true;
        self.pending_cr = false;
    }

    // Clear the entire glyph if either half of a wide character is overwritten.
    fn clear_cell(&mut self, column: usize) {
        if column >= self.cells.len() {
            return;
        }
        let start = if matches!(self.cells[column], Cell::Continuation) {
            column.saturating_sub(1)
        } else {
            column
        };
        if let Cell::Glyph { text, width } = &self.cells[start] {
            self.text_bytes -= text.len();
            let end = (start + width).min(self.cells.len());
            self.cells[start..end].fill(Cell::Blank);
        } else {
            self.cells[column] = Cell::Blank;
        }
    }

    fn write(&mut self, character: char) {
        if self.pending_cr {
            self.rewritten = true;
            self.pending_cr = false;
        }
        let width = character.width().unwrap_or(0);
        self.dirty = true;
        if width == 0 {
            // Combining marks belong to the previous glyph, not a new column.
            let mut column = self.cursor.min(self.cells.len());
            while column > 0 {
                column -= 1;
                match &mut self.cells[column] {
                    Cell::Continuation => continue,
                    Cell::Glyph { text, .. } => {
                        if self.text_bytes + character.len_utf8() <= MAX_TEXT_BYTES {
                            text.push(character);
                            self.text_bytes += character.len_utf8();
                        } else {
                            self.truncated = true;
                        }
                    }
                    Cell::Blank => {}
                }
                break;
            }
            return;
        }
        if self.cursor + width > MAX_COLUMNS {
            self.truncated = true;
            self.cursor = MAX_COLUMNS;
            return;
        }
        let end = self.cursor + width;
        self.cells.resize(self.cells.len().max(end), Cell::Blank);
        for column in self.cursor..end {
            self.clear_cell(column);
        }
        if self.text_bytes + character.len_utf8() <= MAX_TEXT_BYTES {
            self.cells[self.cursor] = Cell::Glyph {
                text: character.to_string(),
                width,
            };
            self.cells[self.cursor + 1..end].fill(Cell::Continuation);
            self.text_bytes += character.len_utf8();
        } else {
            self.truncated = true;
        }
        self.cursor = end;
    }

    fn erase_line(&mut self, kind: u16) {
        let (start, end) = match kind {
            0 => (self.cursor, self.cells.len()),
            1 => (0, (self.cursor + 1).min(self.cells.len())),
            2 => (0, self.cells.len()),
            _ => return,
        };
        for column in start..end {
            self.clear_cell(column);
        }
        if kind == 0 || kind == 2 {
            // EL to the end also erases content beyond our storage limit.
            self.truncated = false;
        }
        while matches!(self.cells.last(), Some(Cell::Blank)) {
            self.cells.pop();
        }
        self.rewritten = true;
        self.pending_cr = false;
        self.dirty = true;
    }
}

impl Perform for LineBuffer {
    fn print(&mut self, character: char) {
        self.write(character);
    }

    fn execute(&mut self, byte: u8) {
        match byte {
            b'\r' => {
                self.cursor = 0;
                // CRLF is a normal line ending. Only subsequent writes rewrite
                // this line; a snapshot at a chunk boundary is still replaceable.
                self.pending_cr = true;
            }
            b'\n' | 0x0b | 0x0c => self.complete(),
            0x08 => self.move_cursor(self.cursor.saturating_sub(1)),
            b'\t' => {
                if self.pending_cr {
                    self.rewritten = true;
                    self.pending_cr = false;
                }
                self.cursor = ((self.cursor / 8 + 1) * 8).min(MAX_COLUMNS);
            }
            _ => {}
        }
    }

    fn csi_dispatch(&mut self, params: &Params, intermediates: &[u8], ignore: bool, action: char) {
        if ignore || !intermediates.is_empty() {
            return;
        }
        let parameter = params
            .iter()
            .next()
            .and_then(|values| values.first())
            .copied()
            .unwrap_or(0);
        let distance = usize::from(parameter.max(1));
        match action {
            'C' | 'a' => self.move_cursor(self.cursor.saturating_add(distance)),
            'D' => self.move_cursor(self.cursor.saturating_sub(distance)),
            'G' | '`' => self.move_cursor(distance - 1),
            'K' => self.erase_line(parameter),
            // SGR, OSC, vertical movement and other terminal operations have no
            // side effects in this log view. vte consumes their complete syntax.
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_existing_webpack_newline_progress_is_classified() {
        assert!(is_webpack_progress("[webpack.Progress] 10% building"));
        assert!(is_webpack_progress("prefix [WEBPACK.PROGRESS] 100%"));
        for message in [
            "10% business report",
            "[webpack.Progress]10%",
            "[webpack.Progress] 1000%",
            "[webpack.Progress] 10%business",
        ] {
            assert!(!is_webpack_progress(message));
        }
    }

    #[cfg(windows)]
    #[test]
    fn windows_shell_pipes_keep_progress_and_final_results_separate() {
        use std::{io::Read, process::Stdio, thread};

        let script = r#"
[Console]::Out.Write("ordinary`r`n")
[Console]::Error.Write("10%`r")
Start-Sleep -Milliseconds 80
[Console]::Error.Write("20%`r")
Start-Sleep -Milliseconds 80
[Console]::Error.Write("`e[2K100%`r`n")
[Console]::Out.Write("Local: http://localhost:8020`r`n")
[Console]::Out.Write("no newline")
exit 7
"#;
        // ESC is built explicitly for Windows PowerShell 5.1 as well as pwsh.
        let script = script.replace("`e", "\u{1b}");
        let mut child = crate::shell_command(&script)
            .unwrap()
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let _job = crate::JobHandle::attach(&child).unwrap();
        fn collect(mut reader: impl Read) -> Vec<LineUpdate> {
            let mut parser = TerminalLogParser::default();
            let mut updates = Vec::new();
            let mut chunk = [0; 17];
            loop {
                let length = reader.read(&mut chunk).unwrap();
                if length == 0 {
                    break;
                }
                updates.extend(parser.feed(&chunk[..length]));
            }
            updates.extend(parser.finish());
            updates
        }
        let stdout = child.stdout.take().unwrap();
        let stderr = child.stderr.take().unwrap();
        let out = thread::spawn(move || collect(stdout));
        let err = thread::spawn(move || collect(stderr));
        assert_eq!(child.wait().unwrap().code(), Some(7));
        let out = out.join().unwrap();
        let err = err.join().unwrap();
        assert!(out.iter().any(|update| update.message == "ordinary"));
        assert!(out
            .iter()
            .any(|update| update.message == "Local: http://localhost:8020"));
        assert_eq!(out.last().unwrap().message, "no newline");
        assert_eq!(err.last().unwrap().message, "100%");
        assert_eq!(err.last().unwrap().mode, "finish");
        assert!(err
            .iter()
            .any(|update| update.message == "10%" && update.mode == "progress"));
        assert!(err
            .iter()
            .all(|update| !update.message.contains(['\r', '\x08', '\x1b'])));
    }

    fn final_message(bytes: &[u8]) -> String {
        let mut parser = TerminalLogParser::default();
        let mut updates = parser.feed(bytes);
        updates.extend(parser.finish());
        updates.pop().unwrap().message
    }

    #[test]
    fn cursor_controls_follow_terminal_overwrite_semantics() {
        for (input, expected) in [
            ("abc\x08X", "abX"),
            ("abcdef\rXY", "XYcdef"),
            ("abcdef\r\x1b[2KXY", "XY"),
            ("abcdef\x1b[3DXY\x1b[K", "abcXY"),
            ("abcdef\x1b[3G\x1b[1KX", "  Xdef"),
            ("a\x1b[2Cb", "a  b"),
            ("a\tb", "a       b"),
            ("abcdefghi\r\tX", "abcdefghX"),
            ("-\x08\\\x08|\x08/", "/"),
            ("中文\x08X", "中 X"),
            ("e\u{301}\x08X", "X"),
        ] {
            assert_eq!(final_message(input.as_bytes()), expected, "{input:?}");
        }
    }

    #[test]
    fn webpack_backspace_redraws_never_accumulate() {
        let mut parser = TerminalLogParser::default();
        let mut old = String::new();
        for progress in 0..=100 {
            let new = format!(" {progress}% building modules {progress}/100 modules 1 active");
            let input = format!(
                "{}{}{}",
                "\x08".repeat(old.len()),
                " \x08".repeat(old.len()),
                new
            );
            let updates = parser.feed(input.as_bytes());
            if progress > 0 {
                assert_eq!(updates.len(), 1);
                assert_eq!(updates[0].mode, "progress");
                assert_eq!(updates[0].message, new);
            }
            old = new;
        }
        assert_eq!(
            parser.feed(b"\n")[0],
            LineUpdate {
                message: old,
                mode: "finish"
            }
        );
        assert!(parser.finish().is_empty());
    }

    #[test]
    fn ordinary_lines_and_split_crlf_do_not_duplicate() {
        let mut parser = TerminalLogParser::default();
        let updates = parser.feed(b"ordinary\r\n\n  spaces  \n");
        assert_eq!(
            updates
                .iter()
                .map(|u| (u.message.as_str(), u.mode))
                .collect::<Vec<_>>(),
            [
                ("ordinary", "append"),
                ("", "append"),
                ("  spaces  ", "append")
            ]
        );
        assert_eq!(parser.feed(b"split\r")[0].message, "split");
        assert_eq!(
            parser.feed(b"\n")[0],
            LineUpdate {
                message: "split".into(),
                mode: "finish"
            }
        );
        assert!(parser.finish().is_empty());
    }

    #[test]
    fn utf8_and_control_sequences_survive_every_chunk_boundary() {
        let input = "\x1b[31m中文进度\x1b[0m\r\x1b[2K完成\x1b]0;ignored title\x07\n";
        for split in 0..=input.len() {
            let mut parser = TerminalLogParser::default();
            let mut updates = parser.feed(&input.as_bytes()[..split]);
            updates.extend(parser.feed(&input.as_bytes()[split..]));
            updates.extend(parser.finish());
            assert_eq!(updates.last().unwrap().message, "完成", "split {split}");
        }
    }

    #[test]
    fn unchanged_frames_are_skipped_and_eof_keeps_the_latest_frame() {
        let mut parser = TerminalLogParser::default();
        assert_eq!(parser.feed(b"10%\r")[0].message, "10%");
        assert!(parser.feed(b"10%\r").is_empty());
        assert_eq!(parser.feed(b"20%\r")[0].message, "20%");
        assert_eq!(
            parser.finish()[0],
            LineUpdate {
                message: "20%".into(),
                mode: "finish"
            }
        );
        assert_eq!(final_message(b"no newline"), "no newline");
        assert_eq!(final_message(b"trailing \xe4\xb8"), "trailing �");
    }

    #[test]
    fn clear_after_publication_updates_the_visible_line() {
        let mut parser = TerminalLogParser::default();
        parser.feed(b"10%\r");
        assert_eq!(parser.feed(b"\x1b[2K")[0].message, "");
        assert_eq!(parser.feed(b"done\n")[0].message, "done");
    }

    #[test]
    fn oversized_lines_and_osc_are_bounded_and_can_recover() {
        let mut parser = TerminalLogParser::default();
        parser.feed(&vec![b'x'; MAX_COLUMNS * 5]);
        assert!(parser.line.cells.len() <= MAX_COLUMNS);
        assert!(parser.line.text_bytes <= MAX_TEXT_BYTES);
        assert!(parser.line.truncated);
        assert_eq!(parser.feed(b"\r\x1b[Kok\n")[0].message, "ok");
        let mut osc = b"\x1b]0;".to_vec();
        osc.extend(vec![b'x'; MAX_COLUMNS * 5]);
        osc.extend(b"\x07visible\n");
        assert_eq!(parser.feed(&osc)[0].message, "visible");
    }
}
