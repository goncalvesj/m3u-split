import { Component } from '@angular/core';
import { ChannelItem } from './Models';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent {
  title = 'm3u-parser';

  channels: ChannelItem[] = [];
  constructor() {}

  addItem(newItem: ChannelItem[]): void {
    this.channels = newItem;

    // this.channels.push(newItem.name);
    // newItem.channels.forEach((element: any) => {
    //   this.typesOfShoes.push(element.name);
    // });
  }
}
