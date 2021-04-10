import {
  Component,
  EventEmitter,
  Inject,
  Input,
  OnInit,
  Output,
} from '@angular/core';
import { HttpClient, HttpEventType } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';

@Component({
  selector: 'file-upload',
  templateUrl: './file-upload.component.html',
  styleUrls: ['./file-upload.component.css'],
})
export class FileUploadComponent implements OnInit {
  @Input()
  requiredFileType: string | undefined;

  @Output() newItemEvent = new EventEmitter<string>();

  fileName = '';
  baseUrl = '';
  uploadProgress: number | undefined;
  uploadSub: Subscription | undefined;

  constructor(private http: HttpClient, @Inject('BASE_URL') baseUrl: string) {
    this.baseUrl = baseUrl;
  }

  ngOnInit(): void {}

  addNewItem(value: string) {
    this.newItemEvent.emit(value);
  }

  onFileSelected(event: any): void {
    const file: File = event.target.files[0];

    console.dir(file);

    if (file) {
      this.fileName = file.name;
      const formData = new FormData();
      formData.append('thumbnail', file);

      const upload$ = this.http
        .post(this.baseUrl + 'upload', formData, {
          reportProgress: true,
          observe: 'events',
        })
        .pipe(finalize(() => this.reset()));

      this.uploadSub = upload$.subscribe((result: any) => {
        if (result.type === HttpEventType.UploadProgress) {
          this.uploadProgress = Math.round(
            100 * (result.loaded / result.total)
          );
        }
        console.dir(result.message);
      });
    }
  }

  cancelUpload(): void {
    this.uploadSub?.unsubscribe();
    this.reset();
  }

  reset(): void {
    this.uploadProgress = undefined;
    this.uploadSub = undefined;
  }
}
