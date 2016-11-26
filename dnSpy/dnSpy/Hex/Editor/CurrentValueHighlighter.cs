﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows.Threading;
using dnSpy.Contracts.Hex;
using dnSpy.Contracts.Hex.Editor;
using dnSpy.Contracts.Hex.Editor.OptionsExtensionMethods;
using dnSpy.Contracts.Hex.Tagging;
using CTC = dnSpy.Contracts.Text.Classification;
using VSTE = Microsoft.VisualStudio.Text.Editor;

namespace dnSpy.Hex.Editor {
	abstract class CurrentValueHighlighterProvider {
		public abstract CurrentValueHighlighter Get(WpfHexView wpfHexView);
	}

	[Export(typeof(CurrentValueHighlighterProvider))]
	sealed class CurrentValueHighlighterProviderImpl : CurrentValueHighlighterProvider {
		public override CurrentValueHighlighter Get(WpfHexView wpfHexView) {
			if (wpfHexView == null)
				throw new ArgumentNullException(nameof(wpfHexView));
			return wpfHexView.Properties.GetOrCreateSingletonProperty(typeof(CurrentValueHighlighter), () => new CurrentValueHighlighter(wpfHexView));
		}
	}

	[Export(typeof(HexViewTaggerProvider))]
	[HexTagType(typeof(HexMarkerTag))]
	sealed class CurrentValueHighlighterHexViewTaggerProvider : HexViewTaggerProvider {
		readonly CurrentValueHighlighterProvider currentValueHighlighterProvider;

		[ImportingConstructor]
		CurrentValueHighlighterHexViewTaggerProvider(CurrentValueHighlighterProvider currentValueHighlighterProvider) {
			this.currentValueHighlighterProvider = currentValueHighlighterProvider;
		}

		public override IHexTagger<T> CreateTagger<T>(HexView hexView, HexBuffer buffer) {
			var wpfHexView = hexView as WpfHexView;
			Debug.Assert(wpfHexView != null);
			if (wpfHexView != null) {
				return wpfHexView.Properties.GetOrCreateSingletonProperty(typeof(CurrentValueHighlighterTagger), () =>
					new CurrentValueHighlighterTagger(currentValueHighlighterProvider.Get(wpfHexView))) as IHexTagger<T>;
			}
			return null;
		}
	}

	sealed class CurrentValueHighlighterTagger : HexTagger<HexMarkerTag> {
		public override event EventHandler<HexBufferSpanEventArgs> TagsChanged;
		readonly CurrentValueHighlighter currentValueHighlighter;

		public CurrentValueHighlighterTagger(CurrentValueHighlighter currentValueHighlighter) {
			if (currentValueHighlighter == null)
				throw new ArgumentNullException(nameof(currentValueHighlighter));
			this.currentValueHighlighter = currentValueHighlighter;
			currentValueHighlighter.Register(this);
		}

		public override IEnumerable<IHexTextTagSpan<HexMarkerTag>> GetTags(HexTaggerContext context) =>
			currentValueHighlighter.GetTags(context);
		public override IEnumerable<IHexTagSpan<HexMarkerTag>> GetTags(NormalizedHexBufferSpanCollection spans) =>
			currentValueHighlighter.GetTags(spans);
		internal void RaiseTagsChanged(HexBufferSpan hexBufferSpan) => TagsChanged?.Invoke(this, new HexBufferSpanEventArgs(hexBufferSpan));
	}

	sealed class CurrentValueHighlighter {
		readonly WpfHexView wpfHexView;
		bool enabled;

		public CurrentValueHighlighter(WpfHexView wpfHexView) {
			if (wpfHexView == null)
				throw new ArgumentNullException(nameof(wpfHexView));
			this.wpfHexView = wpfHexView;
			wpfHexView.Closed += WpfHexView_Closed;
			wpfHexView.Selection.SelectionChanged += Selection_SelectionChanged;
			wpfHexView.Options.OptionChanged += Options_OptionChanged;
			UpdateEnabled();
		}

		void Selection_SelectionChanged(object sender, EventArgs e) => UpdateEnabled();

		void Options_OptionChanged(object sender, VSTE.EditorOptionChangedEventArgs e) {
			if (e.OptionId == DefaultHexViewOptions.HighlightCurrentValueName)
				UpdateEnabled();
		}

		void UpdateEnabled() {
			var newEnabled = wpfHexView.Options.HighlightCurrentValue() && wpfHexView.Selection.IsEmpty;
			if (newEnabled == enabled)
				return;
			enabled = newEnabled;
			if (enabled) {
				HookEvents();
				ReinitializeCurrentValue();
			}
			else {
				UnhookEvents();
				UninitializeCurrentValue();
			}
			RefreshAll();
		}

		void HookEvents() {
			wpfHexView.Caret.PositionChanged += Caret_PositionChanged;
			wpfHexView.BufferLinesChanged += WpfHexView_BufferLinesChanged;
			wpfHexView.Buffer.ChangedLowPriority += Buffer_ChangedLowPriority;
		}

		void UnhookEvents() {
			wpfHexView.Caret.PositionChanged -= Caret_PositionChanged;
			wpfHexView.BufferLinesChanged -= WpfHexView_BufferLinesChanged;
			wpfHexView.Buffer.ChangedLowPriority -= Buffer_ChangedLowPriority;
		}

		void WpfHexView_BufferLinesChanged(object sender, BufferLinesChangedEventArgs e) =>
			wpfHexView.VisualElement.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ReinitializeCurrentValue));

		void RefreshAll() => currentValueHighlighterTagger?.RaiseTagsChanged(new HexBufferSpan(new HexBufferPoint(wpfHexView.Buffer, 0), new HexBufferPoint(wpfHexView.Buffer, HexPosition.MaxEndPosition)));

		void Caret_PositionChanged(object sender, HexCaretPositionChangedEventArgs e) => UpdateCurrentValue();

		void ReinitializeCurrentValue() {
			if (wpfHexView.IsClosed)
				return;
			if (!enabled)
				return;
			savedValue = null;
			UpdateCurrentValue();
		}
		SavedValue savedValue;

		sealed class SavedValue {
			public byte[] Data { get; }
			byte[] TempData { get; }
			public HexBufferSpan BufferSpan { get; private set; }
			public HexColumnType Column { get; }

			public SavedValue(int size, HexCellPosition cellPosition, HexCell cell) {
				if (!cell.HasData)
					throw new InvalidOperationException();
				Data = new byte[size];
				TempData = new byte[size];
				BufferSpan = cell.BufferSpan;
				Column = cellPosition.Column;

				// Note that BufferSpan.Length could be less than Data.Length if cell
				// byte size > 1 and there's not enough bytes at the end of the buffer
				// for the full cell.
				Debug.Assert(BufferSpan.Length <= Data.Length);

				BufferSpan.Buffer.ReadBytes(BufferSpan.Start, Data, 0, Data.Length);
			}

			public bool TryUpdate(HexCellPosition cellPosition, HexBufferLine line, HexCell cell) {
				if (!cell.HasData)
					throw new InvalidOperationException();
				var oldBufferSpan = BufferSpan;
				Debug.Assert(cell.BufferSpan.Length <= Data.Length);
				BufferSpan = cell.BufferSpan;

				bool dataDifferent;
				if (oldBufferSpan != BufferSpan) {
					BufferSpan.Buffer.ReadBytes(BufferSpan.Start, TempData, 0, TempData.Length);
					dataDifferent = !CompareArrays(TempData, Data);
					if (dataDifferent)
						Array.Copy(TempData, Data, Data.Length);
				}
				else
					dataDifferent = false;

				return dataDifferent;
			}

			static bool CompareArrays(byte[] a, byte[] b) {
				if (a.Length != b.Length)
					return false;
				for (int i = 0; i < a.Length; i++) {
					if (a[i] != b[i])
						return false;
				}
				return true;
			}

			public void UpdateValue() {
				Debug.Assert(!BufferSpan.IsDefault);
				BufferSpan.Buffer.ReadBytes(BufferSpan.Start, Data, 0, Data.Length);
			}

			public bool HasSameValueAs(HexBufferLine line, HexCell cell) {
				var index = (long)(cell.BufferStart - line.BufferStart).ToUInt64();
				if (line.HexBytes.AllValid == true) {
					// Nothing to do
				}
				else if (line.HexBytes.AllValid == false) {
					// Never mark non-existent data
					return false;
				}
				else {
					for (long i = 0; i < cell.BufferSpan.Length; i++) {
						if (!line.HexBytes.IsValid(index + i))
							return false;
					}
				}

				line.HexBytes.ReadBytes(index, TempData, 0, TempData.Length);
				return CompareArrays(TempData, Data);
			}
		}

		void UninitializeCurrentValue() => savedValue = null;

		void UpdateCurrentValue() {
			if (wpfHexView.IsClosed)
				return;
			if (!enabled)
				return;

			var bufferLines = wpfHexView.BufferLines;
			var pos = wpfHexView.Caret.Position.Position;
			bool isValues = pos.ActiveColumn == HexColumnType.Values;
			var bufferPos = bufferLines.FilterAndVerify(pos.ActivePosition.BufferPosition);
			var line = wpfHexView.Caret.ContainingHexViewLine.BufferLine;
			var cell = isValues ? line.ValueCells.GetCell(bufferPos) : line.AsciiCells.GetCell(bufferPos);

			if (savedValue == null || savedValue.Column != pos.ActiveColumn)
				savedValue = new SavedValue(isValues ? bufferLines.BytesPerValue : 1, pos.ActivePosition, cell);
			else if (!savedValue.TryUpdate(pos.ActivePosition, line, cell))
				return;
			RefreshAll();
		}

		void Buffer_ChangedLowPriority(object sender, HexContentChangedEventArgs e) {
			if (savedValue != null) {
				foreach (var change in e.Changes) {
					if (savedValue.BufferSpan.Span.OverlapsWith(change.OldSpan)) {
						savedValue.UpdateValue();
						RefreshAll();
						break;
					}
				}
			}
		}

		internal IEnumerable<IHexTextTagSpan<HexMarkerTag>> GetTags(HexTaggerContext context) {
			if (wpfHexView.IsClosed)
				yield break;
			if (!enabled)
				yield break;
			Debug.Assert(savedValue != null);
			if (savedValue == null)
				yield break;
			var cells = (savedValue.Column == HexColumnType.Values ? context.Line.ValueCells : context.Line.AsciiCells).GetVisibleCells();
			var markerTag = savedValue.Column == HexColumnType.Values ? valueCellMarkerTag : asciiCellMarkerTag;
			foreach (var cell in cells) {
				if (savedValue.HasSameValueAs(context.Line, cell))
					yield return new HexTextTagSpan<HexMarkerTag>(cell.CellSpan, markerTag);
			}
		}
		static readonly HexMarkerTag valueCellMarkerTag = new HexMarkerTag(CTC.ThemeClassificationTypeNameKeys.HexCurrentValueCell);
		static readonly HexMarkerTag asciiCellMarkerTag = new HexMarkerTag(CTC.ThemeClassificationTypeNameKeys.HexCurrentAsciiCell);

		internal IEnumerable<IHexTagSpan<HexMarkerTag>> GetTags(NormalizedHexBufferSpanCollection spans) {
			yield break;
		}

		internal void Register(CurrentValueHighlighterTagger currentValueHighlighterTagger) {
			if (currentValueHighlighterTagger == null)
				throw new ArgumentNullException(nameof(currentValueHighlighterTagger));
			if (this.currentValueHighlighterTagger != null)
				throw new InvalidOperationException();
			this.currentValueHighlighterTagger = currentValueHighlighterTagger;
		}
		CurrentValueHighlighterTagger currentValueHighlighterTagger;

		void WpfHexView_Closed(object sender, EventArgs e) {
			wpfHexView.Closed -= WpfHexView_Closed;
			wpfHexView.Selection.SelectionChanged -= Selection_SelectionChanged;
			wpfHexView.Options.OptionChanged -= Options_OptionChanged;
			UninitializeCurrentValue();
			UnhookEvents();
		}
	}
}