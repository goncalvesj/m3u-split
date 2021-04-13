import { Component, Input, OnInit } from '@angular/core';
import { ChannelsList } from '../Models';
import { saveAs } from 'file-saver';

@Component({
  selector: 'file-download',
  templateUrl: './file-download.component.html',
  styleUrls: ['./file-download.component.css'],
})
export class FileDownloadComponent implements OnInit {
  @Input()
  channelsList: ChannelsList | undefined;

  constructor() {}

  ngOnInit(): void {}

  saveFile(): void {
    const channelsToExtract = [2, 216];
    let outputLines: string[] = [];

    for (const channel of channelsToExtract) {
      const nextIndex = this.channelsList?.channels.find((x) => x.index > channel);
      console.log(channel);
      console.log(nextIndex);

      const output = this.channelsList?.channelsArray.slice(
        channel - 1,
        (nextIndex?.index as number) - 1
      );
      outputLines = outputLines.concat(output as string[]);
    }

    const blob = new Blob([outputLines.join('\n')], {
      type: 'audio/x-mpegurl',
    });
    saveAs(blob, 'channels.m3u');
  }
}
