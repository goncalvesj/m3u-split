import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { ChannelItem } from '../Models';
import { saveAs } from 'file-saver';

@Component({
  selector: 'file-upload',
  templateUrl: './file-upload.component.html',
  styleUrls: ['./file-upload.component.css'],
})
export class FileUploadComponent implements OnInit {
  @Input()
  requiredFileType: string | undefined;

  @Output() newItemEvent = new EventEmitter<ChannelItem[]>();

  fileName = '';
  fileContent = '';
  fileContentArray: string[] = [];
  channels: ChannelItem[] = [];

  constructor() {}

  ngOnInit(): void {}

  populateList(value: ChannelItem[]): void {
    this.newItemEvent.emit(value);
  }

  lineFromPos(input: string, indexPosition: number): number {
    let lineNumber = 1;
    for (let i = 0; i < indexPosition; i++) {
      if (input[i] === '\n') {
        lineNumber++;
      }
    }
    return lineNumber;
  }

  readThis(inputValue: File): void {
    const myReader: FileReader = new FileReader();

    myReader.onloadend = (e) => {
      this.fileContent = myReader.result as string;
      this.fileContentArray = this.fileContent.split(/\r?\n/);

      const regex = /\btvg-name="----([^""]+)".*\r?\n/g;
      for (const match of this.fileContent.matchAll(regex)) {
        const clean = match[1].replaceAll('-', '').replaceAll(' ', '');
        const index = this.lineFromPos(this.fileContent, match.index as number);

        this.channels.push({
          index,
          name: clean,
        });
      }

      this.populateList(this.channels);
    };

    myReader.readAsText(inputValue);
  }

  saveFile(): void {
    const channelsToExtract = [2, 216];
    let outputLines: string[] = [];

    for (const channel of channelsToExtract) {
      const nextIndex = this.channels.find((x) => x.index > channel);
      console.log(channel);
      console.log(nextIndex);

      const output = this.fileContentArray.slice(
        channel - 1,
        (nextIndex?.index as number) - 1
      );
      outputLines = outputLines.concat(output);
    }

    const blob = new Blob([outputLines.join('\n')], {
      type: 'audio/x-mpegurl',
    });
    saveAs(blob, 'channels.m3u');
  }

  onFileSelected(event: any): void {
    const file: File = event.target.files[0];

    if (file) {
      this.fileName = file.name;
      this.readThis(file);
    }
  }
}
