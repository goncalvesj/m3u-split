import { Component } from '@angular/core';
import { MatListOption } from '@angular/material/list';
import { ChannelsList } from './Models';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent {
  title = 'm3u-parser';

  channelsList: ChannelsList | undefined;
  selectedOptions: number[] = [];
  constructor() {}

  addItem(newItem: ChannelsList): void {
    this.channelsList = newItem;
  }

  selectionChange(option: MatListOption): void {
    console.log(option.value);
  }
}
