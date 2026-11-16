import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ChatResponse, DocumentDto } from './api.service';

interface ChatTurn {
  question: string;
  response?: ChatResponse;
  error?: string;
}

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private pollTimer?: ReturnType<typeof setInterval>;

  protected readonly documents = signal<DocumentDto[]>([]);
  protected readonly uploadError = signal<string | null>(null);
  protected readonly uploading = signal(false);
  protected readonly turns = signal<ChatTurn[]>([]);
  protected readonly asking = signal(false);
  protected question = '';

  protected readonly hasIndexedDocuments = computed(() =>
    this.documents().some((d) => d.status === 'Indexed'),
  );

  ngOnInit(): void {
    this.refresh();
    // Poll so indexing status updates without a manual reload; cheap for an MVP.
    this.pollTimer = setInterval(() => {
      if (this.documents().some((d) => d.status === 'Pending' || d.status === 'Indexing')) {
        this.refresh();
      }
    }, 2000);
  }

  ngOnDestroy(): void {
    clearInterval(this.pollTimer);
  }

  protected refresh(): void {
    this.api.listDocuments().subscribe({
      next: (docs) => this.documents.set(docs),
      error: () => this.uploadError.set('Cannot reach the API. Is the backend running on port 5080?'),
    });
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.uploadError.set(null);
    this.uploading.set(true);
    this.api.upload(file).subscribe({
      next: () => {
        this.uploading.set(false);
        input.value = '';
        this.refresh();
      },
      error: (e: HttpErrorResponse) => {
        this.uploading.set(false);
        input.value = '';
        this.uploadError.set(e.error?.error ?? 'Upload failed.');
      },
    });
  }

  protected remove(doc: DocumentDto): void {
    this.uploadError.set(null);
    this.api.deleteDocument(doc.id).subscribe({
      next: () => this.refresh(),
      error: (e: HttpErrorResponse) => this.uploadError.set(e.error?.error ?? 'Delete failed.'),
    });
  }

  protected ask(): void {
    const question = this.question.trim();
    if (!question || this.asking()) return;

    const turn: ChatTurn = { question };
    this.turns.update((t) => [...t, turn]);
    this.question = '';
    this.asking.set(true);

    this.api.ask(question).subscribe({
      next: (response) => this.finishTurn(turn, { response }),
      error: (e: HttpErrorResponse) =>
        this.finishTurn(turn, { error: e.error?.error ?? 'The request failed.' }),
    });
  }

  private finishTurn(turn: ChatTurn, result: Partial<ChatTurn>): void {
    this.turns.update((t) => t.map((x) => (x === turn ? { ...x, ...result } : x)));
    this.asking.set(false);
  }
}
