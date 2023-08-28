// import {
//   Component,
//   EventEmitter,
//   Inject,
//   Input,
//   OnInit,
//   Output,
// } from '@angular/core';
// import { HttpClient, HttpEvent, HttpEventType } from '@angular/common/http';
// import { Subscription } from 'rxjs';
// import { ChannelItem } from '../ApiResult';
// import { saveAs } from 'file-saver';

// @Component({
//   selector: 'file-upload',
//   templateUrl: './file-upload.component.html',
//   styleUrls: ['./file-upload.component.css'],
// })
// export class FileUploadComponent implements OnInit {
//   @Input()
//   requiredFileType: string | undefined;

//   @Output() newItemEvent = new EventEmitter<ChannelItem>();

//   fileName = '';
//   baseUrl = '';
//   uploadProgress: number | undefined;
//   uploadSub: Subscription | undefined;

//   constructor(private http: HttpClient, @Inject('BASE_URL') baseUrl: string) {
//     this.baseUrl = baseUrl;
//   }

//   ngOnInit(): void {}

//   populateList(value: ChannelItem): void {
//     this.newItemEvent.emit(value);
//   }

//   lineFromPos(input: string, indexPosition: number): number {
//     let lineNumber = 1;
//     for (let i = 0; i < indexPosition; i++) {
//       if (input[i] === '\n') {
//         lineNumber++;
//       }
//     }
//     return lineNumber;
//   }
//   readThis(inputValue: any): void {
//     const file: File = inputValue.target.files[0];
//     const myReader: FileReader = new FileReader();

//     myReader.onloadend = (e) => {
//       // you can perform an action with readed data here
//       // console.log(myReader.result);

//       const text = myReader.result as string;
//       // const pattern = '\btvg-name=""----([^""]+)"".*\r?\n';
//       // const regexp = RegExp('\btvg-name=""----([^""]+)"".*\r?\n', '');

//       // const array = [...text.matchAll(regexp)];
//       // console.log(array[0]);

//       // const text = 'Magic hex numbers: DEADBEEF CAFE';

//       const channels: ChannelItem[] = [];
//       const regex = /\btvg-name="----([^""]+)".*\r?\n/g;
//       for (const match of text.matchAll(regex)) {
//         const clean = match[1].replaceAll('-', '').replaceAll(' ', '');

//         const index = this.lineFromPos(text, match.index as number);

//         channels.push({
//           index,
//           name: clean,
//         });

//         this.populateList({
//           index,
//           name: clean,
//         });
//         // console.dir({
//         //   index,
//         //   name: clean,
//         // });

//         // console.log(index);
//         // console.log(clean);
//       }
//       // const array = [...text.matchAll(regex)];
//       // console.log(array[0]);

//       // while ((match = regex.exec(text))) {
//       //   console.log(match);
//       // }
//       // const data = new Blob([text], {type: 'text/plain'});
//       const blob = new Blob([text], {
//         type: 'audio/x-mpegurl',
//       });
//       saveAs(blob, 'channels.m3u');
//     };

//     myReader.readAsText(file);
//   }

//   onFileSelected(event: any): void {
//     const file: File = event.target.files[0];

//     console.dir(file);

//     this.readThis(event);

//     // if (file) {
//     //   this.fileName = file.name;
//     //   const formData = new FormData();
//     //   formData.append('thumbnail', file);

//     //   const upload$ = this.http
//     //     .post(this.baseUrl + 'upload', formData, {
//     //       reportProgress: true,
//     //       observe: 'events',
//     //     })
//     //     .pipe(finalize(() => this.reset()));

//     //   this.uploadSub = upload$.subscribe((result: HttpEvent<object>) => {
//     //     if (result.type === HttpEventType.UploadProgress && result.total) {
//     //       this.uploadProgress = Math.round(
//     //         100 * (result.loaded / result.total)
//     //       );
//     //     }
//     //     if (result.type === HttpEventType.Response) {
//     //       const apiResult = result.body as ApiResult;
//     //       console.dir(apiResult.channels);
//     //       this.populateList(apiResult);
//     //     }
//     //   });
//     // }
//   }

//   cancelUpload(): void {
//     this.uploadSub?.unsubscribe();
//     this.reset();
//   }

//   reset(): void {
//     this.uploadProgress = undefined;
//     this.uploadSub = undefined;
//   }
// }
