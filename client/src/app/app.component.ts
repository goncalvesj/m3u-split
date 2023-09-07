import { Component } from '@angular/core';
import { HttpClient, HttpRequest } from '@angular/common/http';
import { saveAs } from 'file-saver';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent {
  title = 'm3u-split';
  categories: any = {};
  fileName: string = '';
  isUploading: boolean = false;
  selectedOptions: any[] = [];
  message = '';

  constructor(private http: HttpClient) {
  }

  upload(input: any) {
    const formData: FormData = new FormData();

    const file: File = input.target.files[0];
    formData.append('file', file);

    const req = new HttpRequest('POST', '/api/upload', formData, {
      responseType: 'json',
    });

    this.http.request(req).subscribe((resp: any) => {
      if (resp.body) {
        this.categories = resp.body.categories;
        this.fileName = resp.body.fileName;
      }
    });
  }

  onDownloadApi() {
    const data = {
      fileName: this.fileName,
      categories: this.selectedOptions,
    };
    this.http
      .post('/api/download', data, { responseType: 'text' })
      .subscribe((resp: any) => {
        const blob = new Blob([resp], {
          type: 'audio/x-mpegurl',
        });
        saveAs(blob, 'channels.m3u');
      });
  }
}
