import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export type DocumentStatus = 'Pending' | 'Indexing' | 'Indexed' | 'Failed';

export interface DocumentDto {
  id: string;
  fileName: string;
  status: DocumentStatus;
  chunkCount: number;
  error: string | null;
  uploadedAt: string;
}

export interface Citation {
  sourceNumber: number;
  documentName: string;
  pageNumber: number | null;
  chunkIndex: number;
  score: number;
  snippet: string;
}

export interface ChatResponse {
  answer: string;
  insufficientContext: boolean;
  citations: Citation[];
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  listDocuments(): Observable<DocumentDto[]> {
    return this.http.get<DocumentDto[]>('/api/documents');
  }

  upload(file: File): Observable<DocumentDto> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<DocumentDto>('/api/documents', form);
  }

  deleteDocument(id: string): Observable<void> {
    return this.http.delete<void>(`/api/documents/${id}`);
  }

  ask(question: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>('/api/chat', { question });
  }
}
